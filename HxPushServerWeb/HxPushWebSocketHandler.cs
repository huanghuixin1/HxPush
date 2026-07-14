using System.Net.WebSockets;
using System.Text;

namespace HxPushServerWeb
{
    // 负责 /ws：升级 WebSocket、接收消息、回发 echo、处理断开。
    internal sealed class HxPushWebSocketHandler
    {
        public async Task HandleAsync(HttpContext context)
        {
            // 普通 HTTP 请求不能直接当成 WebSocket 用。
            // 这里返回 200 + HxHttpResModel JSON，和其它 HTTP 接口保持一致。
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await HxPushHttpHandler.ToJsonResult(
                    HxPushHttpHandler.Error("Please connect with WebSocket.")).ExecuteAsync(context);
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            // 同一个 WebSocket 不能并发 SendAsync，所以所有发送都经过这个锁。
            using var sendLock = new SemaphoreSlim(1, 1);
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            await SendTextAsync(webSocket, "connected", sendLock, connectionCancellation.Token);
            Console.WriteLine("客户端链接成功");

            // 示例推送：服务端每 5 秒主动发一条消息，方便页面观察长连接。
            var serverPushTask = Task.Run(async () =>
            {
                while (webSocket.State == WebSocketState.Open && !connectionCancellation.IsCancellationRequested)
                {
                    await SendTextAsync(webSocket, "serverMsg" + DateTime.Now.ToString("HH:mm:ss"), sendLock, connectionCancellation.Token);
                    await Task.Delay(TimeSpan.FromSeconds(5), connectionCancellation.Token);
                }
            }, connectionCancellation.Token);

            try
            {
                await ReceiveLoopAsync(webSocket, sendLock, connectionCancellation.Token);
            }
            finally
            {
                // 接收循环结束后，通知后台推送任务也退出。
                connectionCancellation.Cancel();
            }

            try
            {
                await serverPushTask;
            }
            catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
            {
            }
            catch (WebSocketException) when (webSocket.State != WebSocketState.Open)
            {
            }
        }

        private static async Task ReceiveLoopAsync(WebSocket webSocket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 4];

            // 只处理文本消息；收到 close 或 exit 就正常断开。
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closed by client",
                        cancellationToken);
                    Console.WriteLine("客户端断开");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "text message only",
                        cancellationToken);
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("接收到消息" + text);

                if (text == "exit")
                {
                    // 服务端主动关闭后必须 break，不能再继续 SendAsync。
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closed by server(client send exit)",
                        cancellationToken);
                    Console.WriteLine("客户端主动断开");
                    break;
                }

                await SendTextAsync(webSocket, $"echo: {text}", sendLock, cancellationToken);
            }
        }

        private static async Task SendTextAsync(
            WebSocket webSocket,
            string text,
            SemaphoreSlim sendLock,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            // 串行化发送，并在发送前确认连接仍然打开。
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        WebSocketMessageFlags.EndOfMessage,
                        cancellationToken);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
