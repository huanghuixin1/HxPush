using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace HxPushServer
{
    internal class Program
    {
        private const int DefaultPort = 5000;
        private const string WebSocketPath = "/ws";

        private static async Task Main(string[] args)
        {
            var urlPrefixes = args.Length > 0
                ? args.Select(NormalizeUrlPrefix)
                : GetDefaultUrlPrefixes(DefaultPort);

            using var server = new SimpleWebSocketServer(urlPrefixes, WebSocketPath);
            using var cancellationTokenSource = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            await server.RunAsync(cancellationTokenSource.Token);
        }

        private static IEnumerable<string> GetDefaultUrlPrefixes(int port)
        {
            yield return $"http://localhost:{port}/";

            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address)
                .Where(address => !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct();

            foreach (var address in localAddresses)
            {
                yield return $"http://{address}:{port}/";
            }
        }

        private static string NormalizeUrlPrefix(string urlPrefix)
        {
            return urlPrefix.EndsWith('/') ? urlPrefix : $"{urlPrefix}/";
        }
    }

    internal sealed class SimpleWebSocketServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly ConcurrentDictionary<Guid, WebSocket> clients = new();
        private readonly string webSocketPath;

        public SimpleWebSocketServer(IEnumerable<string> urlPrefixes, string webSocketPath)
        {
            foreach (var urlPrefix in urlPrefixes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                listener.Prefixes.Add(urlPrefix);
            }

            this.webSocketPath = webSocketPath;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException exception) when (exception.ErrorCode == 5)
            {
                Console.Error.WriteLine("Start failed: listening on LAN IP may require administrator permission, URL reservation, or firewall access.");
                throw;
            }

            using var stopRegistration = cancellationToken.Register(listener.Stop);

            Console.WriteLine("HxPush WebSocket server started.");
            Console.WriteLine("HTTP endpoints:");

            foreach (var prefix in listener.Prefixes)
            {
                Console.WriteLine($"  {prefix}");
            }

            Console.WriteLine("WebSocket endpoints:");

            foreach (var prefix in listener.Prefixes)
            {
                Console.WriteLine($"  {ToWebSocketUrl(prefix)}");
            }

            Console.WriteLine("Press Ctrl+C to stop.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            await CloseAllClientsAsync(CancellationToken.None);
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            if (context.Request.Url?.AbsolutePath == "/")
            {
                await WriteTextAsync(context.Response, "HxPushServer is running. Connect WebSocket at /ws.", HttpStatusCode.OK);
                return;
            }

            if (context.Request.Url?.AbsolutePath != webSocketPath || !context.Request.IsWebSocketRequest)
            {
                await WriteTextAsync(context.Response, "Not found.", HttpStatusCode.NotFound);
                return;
            }

            var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            var clientId = Guid.NewGuid();
            clients[clientId] = webSocketContext.WebSocket;

            Console.WriteLine($"Client connected: {clientId}");
            await SendTextAsync(webSocketContext.WebSocket, $"Connected to HxPushServer as {clientId}", cancellationToken);

            try
            {
                await ReceiveLoopAsync(clientId, webSocketContext.WebSocket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                clients.TryRemove(clientId, out _);
                await CloseSocketAsync(webSocketContext.WebSocket, cancellationToken);
                Console.WriteLine($"Client disconnected: {clientId}");
            }
        }

        private async Task ReceiveLoopAsync(Guid clientId, WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported.", cancellationToken);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(message.ToArray());
                Console.WriteLine($"[{clientId}] {text}");
                await BroadcastAsync($"{clientId}: {text}", cancellationToken);
            }
        }

        private async Task BroadcastAsync(string text, CancellationToken cancellationToken)
        {
            var disconnectedClients = new List<Guid>();

            foreach (var (clientId, client) in clients)
            {
                if (client.State != WebSocketState.Open)
                {
                    disconnectedClients.Add(clientId);
                    continue;
                }

                try
                {
                    await SendTextAsync(client, text, cancellationToken);
                }
                catch (WebSocketException)
                {
                    disconnectedClients.Add(clientId);
                }
            }

            foreach (var clientId in disconnectedClients)
            {
                clients.TryRemove(clientId, out _);
            }
        }

        private static Task SendTextAsync(WebSocket webSocket, string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, string text, HttpStatusCode statusCode)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private async Task CloseAllClientsAsync(CancellationToken cancellationToken)
        {
            foreach (var (_, client) in clients)
            {
                await CloseSocketAsync(client, cancellationToken);
            }

            clients.Clear();
        }

        private static async Task CloseSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing.", cancellationToken);
            }

            webSocket.Dispose();
        }

        public void Dispose()
        {
            listener.Close();
        }

        private string ToWebSocketUrl(string urlPrefix)
        {
            var webSocketPrefix = urlPrefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"wss://{urlPrefix["https://".Length..]}"
                : $"ws://{urlPrefix["http://".Length..]}";

            return $"{webSocketPrefix.TrimEnd('/')}{webSocketPath}";
        }
    }
}
