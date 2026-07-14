using HxPushApp.Helpers;

namespace HxPushApp
{
    public partial class SettingsPage : ContentPage
    {
        private const int MaxWebSocketLogLines = 30;
        private static readonly Uri WebSocketServerUri = new("ws://192.168.31.119:5212/ws");

        private readonly Queue<string> webSocketLogLines = new();
        private readonly WebSocketClientHelper webSocketClient = new(WebSocketServerUri);

        public SettingsPage()
        {
            InitializeComponent();

            // WebSocketClientHelper 只负责通信；页面订阅事件后统一把消息写到日志区域。
            webSocketClient.StatusChanged += (_, message) => AppendWebSocketLog(message);
            webSocketClient.TextMessageReceived += (_, message) => AppendWebSocketLog($"接收：{message}");
            webSocketClient.BinaryMessageReceived += (_, length) => AppendWebSocketLog($"接收：{length} 字节二进制消息。");
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // 离开设置页时断开连接，避免后台接收任务继续占用资源。
            _ = webSocketClient.DisconnectAsync();
        }

        /// <summary>
        /// 发送输入框中的 WebSocket 测试消息。
        /// </summary>
        private async void BtnTestSendWsMsg_Clicked(object? sender, EventArgs e)
        {
            BtnTestSendWsMsg.IsEnabled = false;

            try
            {
                var message = EditTestSendWsMsg.Text?.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    AppendWebSocketLog("请输入要发送的消息。");
                    return;
                }

                await webSocketClient.ConnectAsync();
                await webSocketClient.SendTextAsync(message);
                AppendWebSocketLog($"发送：{message}");
            }
            catch (OperationCanceledException)
            {
                AppendWebSocketLog("发送失败：连接或发送超时。");
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"发送失败：{ex.Message}");
            }
            finally
            {
                BtnTestSendWsMsg.IsEnabled = true;
            }
        }

        /// <summary>
        /// 建立 WebSocket 测试连接，并发送一条默认测试消息。
        /// </summary>
        private async void OnWebSocketTestClicked(object? sender, EventArgs e)
        {
            WebSocketTestButton.IsEnabled = false;

            try
            {
                await webSocketClient.ConnectAsync();

                var testMessage = $"HxPushApp test {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                await webSocketClient.SendTextAsync(testMessage);
                AppendWebSocketLog($"发送：{testMessage}");
                AppendWebSocketLog("连接已保持，正在持续接收服务端消息。");
            }
            catch (OperationCanceledException)
            {
                AppendWebSocketLog("通信失败：连接或等待响应超时。");
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"通信失败：{ex.Message}");
            }
            finally
            {
                // 连接成功后禁用测试连接按钮，后续直接用发送按钮复用现有连接。
                WebSocketTestButton.IsEnabled = !webSocketClient.IsConnected;
            }
        }

        /// <summary>
        /// 追加 WebSocket 日志，只保留最近若干行，避免 Label 内容无限增长。
        /// </summary>
        private void AppendWebSocketLog(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                webSocketLogLines.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");

                while (webSocketLogLines.Count > MaxWebSocketLogLines)
                {
                    webSocketLogLines.Dequeue();
                }

                WebSocketTestResultLabel.Text = string.Join(Environment.NewLine, webSocketLogLines);
            });
        }
    }
}
