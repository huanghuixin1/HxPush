using System.Net.WebSockets;
using System.Text;

namespace HxPushServerWeb
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Listen on all network interfaces by default.
            // LAN devices should visit this server with the machine IP, for example:
            // http://192.168.1.10:5212/ws-test.html
            if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            {
                builder.WebHost.UseUrls("http://0.0.0.0:5212");
            }

            var app = builder.Build();

            app.UseStaticFiles();

            // 第一步：打开 ASP.NET Core 的 WebSocket 支持。
            // 没有这一句，浏览器发来的 ws:// 连接不会被升级成 WebSocket。
            app.UseWebSockets();

            app.MapGet("/", () => Results.Text(
                "HxPushServerWeb is running. Open /ws-test.html or connect WebSocket at /ws.",
                "text/plain; charset=utf-8"));

            // 第二步：约定 WebSocket 连接地址是 /ws。
            app.Map("/ws", async context =>
            {
                // 普通 HTTP 请求不能直接当成 WebSocket 用。
                // 这里做一次判断，避免用户在浏览器地址栏直接打开 /ws 时卡住。
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Please connect with WebSocket.");
                    return;
                }

                // 第三步：接受连接。
                // 从这里开始，webSocket 就代表一个已经连上的客户端。
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await SendTextAsync(webSocket, "connected", context.RequestAborted);
                Console.WriteLine("客户端链接成功");

                // 第四步：循环读取客户端发来的消息。
                var buffer = new byte[1024 * 4];
                ThreadPool.QueueUserWorkItem(async a => {
                    while (webSocket.State == WebSocketState.Open)
                    {
                        await SendTextAsync(webSocket, "serverMsg" + DateTime.Now.ToString("HH:mm:ss"), context.RequestAborted);

                        Thread.Sleep(5000);
                    }
                });

                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(buffer, context.RequestAborted);

                    // 客户端主动关闭连接时，服务端也正常关闭。
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "closed by client",
                            context.RequestAborted);
                        Console.WriteLine("客户端断开");
                        break;
                    }

                    // 这个最小示例只处理文本消息。
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "text message only",
                            context.RequestAborted);
                        break;
                    }

                    // 第五步：把收到的字节转成字符串，然后回发给客户端。
                    // 这就是最简单的 Echo Server：你发什么，服务器回什么。
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine("接收到消息" + text);
                    if (text == "exit")
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "closed by server(client send exit)",
                            context.RequestAborted);
                        Console.WriteLine("客户端主动断开");
                        // CloseAsync 之后连接已经开始关闭，不能再继续 SendAsync。
                        // 所以这里要跳出循环，否则下面的 echo 回发会因为连接已关闭而报异常。
                        break;
                    }

                    await SendTextAsync(webSocket, $"echo: {text}", context.RequestAborted);
                }
            });

            await app.RunAsync();
        }

        private static async Task SendTextAsync(WebSocket webSocket, string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            await webSocket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                WebSocketMessageFlags.EndOfMessage,
                cancellationToken);
        }
    }
}
