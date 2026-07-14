using System.Net.WebSockets;
using System.Text;

namespace HxPushApp
{
    public partial class SettingsPage : ContentPage
    {
        private static readonly Uri WebSocketServerUri = new("ws://192.168.31.119:5212/ws");

        public SettingsPage()
        {
            InitializeComponent();
        }

        private async void OnWebSocketTestClicked(object? sender, EventArgs e)
        {
            WebSocketTestButton.IsEnabled = false;
            WebSocketTestResultLabel.Text = $"正在连接 {WebSocketServerUri} ...";

            try
            {
                var result = await TestWebSocketAsync();
                WebSocketTestResultLabel.Text = $"通信成功\n{result}";
            }
            catch (OperationCanceledException)
            {
                WebSocketTestResultLabel.Text = "通信失败：连接或等待响应超时。";
            }
            catch (Exception ex)
            {
                WebSocketTestResultLabel.Text = $"通信失败：{ex.Message}";
            }
            finally
            {
                WebSocketTestButton.IsEnabled = true;
            }
        }

        private static async Task<string> TestWebSocketAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var webSocket = new ClientWebSocket();

            await webSocket.ConnectAsync(WebSocketServerUri, timeout.Token);
            var connectedMessage = await ReceiveTextAsync(webSocket, timeout.Token);

            var testMessage = $"HxPushApp test {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var messageBytes = Encoding.UTF8.GetBytes(testMessage);
            await webSocket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeout.Token);

            var responseMessage = await ReceiveTextAsync(webSocket, timeout.Token);

            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Test completed",
                closeTimeout.Token);

            return $"服务端：{connectedMessage}\n发送：{testMessage}\n接收：{responseMessage}";
        }

        private static async Task<string> ReceiveTextAsync(
            ClientWebSocket webSocket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();

            while (true)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("服务端已关闭 WebSocket 连接。");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidOperationException("服务端返回了非文本消息。");
                }

                message.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }
    }
}
