using HxPushApp.Helpers;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;
using System.Text.Json;

namespace HxPushApp
{
    public partial class SettingsPage : ContentPage
    {
        private const int MaxWebSocketLogLines = 30;
        private static readonly Uri WebSocketServerUri = new("ws://192.168.31.119:5212/ws");

        private readonly Queue<string> webSocketLogLines = new();
        private readonly WebSocketClientHelper webSocketClient = new(WebSocketServerUri);
        private readonly SqliteHelper sqliteHelper = SqliteHelper.Instance;

        public SettingsPage()
        {
            InitializeComponent();

            AppKeyEntry.Text = AppSettings.AppKey;

            // WebSocketClientHelper 只负责通信；页面订阅事件后统一把消息写到日志区域。
            webSocketClient.StatusChanged += (_, message) => AppendWebSocketLog(message);
            webSocketClient.ConnectionStateChanged += (_, isConnected) =>
                UpdateWebSocketButtonStates(isConnected);
            webSocketClient.TextMessageReceived += async (_, message) => await HandleWebSocketTextMessageAsync(message);
            webSocketClient.BinaryMessageReceived += (_, length) => AppendWebSocketLog($"接收：{length} 字节二进制消息。");

            UpdateWebSocketButtonStates(webSocketClient.IsConnected);
        }

        /// <summary>
        /// 保存 AppKey 到 MAUI Preferences。空值不会覆盖已经保存的配置。
        /// </summary>
        private void OnSaveAppKeyClicked(object? sender, EventArgs e)
        {
            var appKey = AppKeyEntry.Text?.Trim();
            if (string.IsNullOrWhiteSpace(appKey))
            {
                AppKeySaveStatusLabel.Text = "AppKey 不能为空。";
                AppKeySaveStatusLabel.TextColor = Colors.IndianRed;
                AppKeyEntry.Focus();
                return;
            }

            try
            {
                AppSettings.AppKey = appKey;
                AppKeyEntry.Text = appKey;
                AppKeySaveStatusLabel.Text = "AppKey 已保存到本机。";
                AppKeySaveStatusLabel.TextColor = Colors.ForestGreen;
            }
            catch (Exception ex)
            {
                AppKeySaveStatusLabel.Text = $"保存失败：{ex.Message}";
                AppKeySaveStatusLabel.TextColor = Colors.IndianRed;
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // 离开设置页时断开连接，避免后台接收任务继续占用资源。
            _ = webSocketClient.DisconnectAsync();
        }

        /// <summary>
        /// 主动断开当前 WebSocket 测试连接。
        /// </summary>
        private async void OnWebSocketDisconnectClicked(object? sender, EventArgs e)
        {
            WebSocketDisconnectButton.IsEnabled = false;

            try
            {
                await webSocketClient.DisconnectAsync();
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"断开失败：{ex.Message}");
            }
            finally
            {
                UpdateWebSocketButtonStates(webSocketClient.IsConnected);
            }
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

                var pushMessage = CreateTestPushMessage(message);
                var payload = JsonSerializer.Serialize(pushMessage);

                // 手动发送前也使用已保存 AppKey 完成握手校验。
                await webSocketClient.ConnectAsync(AppSettings.AppKey);
                await webSocketClient.SendTextAsync(payload);
                AppendWebSocketLog($"发送：{payload}");
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
        /// 使用已保存 AppKey 建立 WebSocket 测试连接并保持接收。
        /// </summary>
        private async void OnWebSocketTestClicked(object? sender, EventArgs e)
        {
            WebSocketTestButton.IsEnabled = false;

            try
            {
                // AppKey 随握手 URL 提交，连接成功即表示服务端校验通过。
                await webSocketClient.ConnectAsync(AppSettings.AppKey);
                AppendWebSocketLog("AppKey 校验通过，连接已保持，正在持续接收服务端消息。");
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
                UpdateWebSocketButtonStates(webSocketClient.IsConnected);
            }
        }

        /// <summary>
        /// 根据连接状态切换测试连接和断开连接按钮。
        /// </summary>
        private void UpdateWebSocketButtonStates(bool isConnected)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                WebSocketTestButton.IsEnabled = !isConnected;
                WebSocketDisconnectButton.IsEnabled = isConnected;
            });
        }

        /// <summary>
        /// 创建完整的测试推送模型。AppKey 使用已保存配置，Hwid 使用当前安装的稳定标识。
        /// </summary>
        private static HxPushMsgModel CreateTestPushMessage(string message)
        {
            var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return new HxPushMsgModel
            {
                ID = Guid.NewGuid().ToString("N"),
                AppKey = AppSettings.AppKey,
                Hwid = AppSettings.DeviceId,
                MsgDate = unixSeconds > int.MaxValue ? int.MaxValue : (int)unixSeconds,
                Msg = message
            };
        }

        /// <summary>
        /// 处理 WebSocket 文本消息：先展示日志，再尝试按 HxPushMsgModel JSON 保存到 SQLite。
        /// WebSocketClientHelper 不知道 SQLite，SqliteHelper 也不知道 WebSocket，这里只做流程编排。
        /// </summary>
        private async Task HandleWebSocketTextMessageAsync(string message)
        {
            AppendWebSocketLog($"接收：{message}");

            if (!TryParsePushMessage(message, out var pushMessage))
            {
                return;
            }

            try
            {
                await sqliteHelper.SaveMessageAsync(pushMessage);
                AppendWebSocketLog($"已保存消息：{pushMessage.ID}");
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"保存消息失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 将服务端文本解析为推送消息模型；普通 echo、connected 等非 JSON 文本不会入库。
        /// </summary>
        private static bool TryParsePushMessage(
            string message,
            out HxPushMsgModel pushMessage)
        {
            pushMessage = new HxPushMsgModel();

            if (string.IsNullOrWhiteSpace(message) || !message.TrimStart().StartsWith('{'))
            {
                return false;
            }

            try
            {
                var parsedMessage = JsonSerializer.Deserialize<HxPushMsgModel>(
                    message,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (parsedMessage is null ||
                    string.IsNullOrWhiteSpace(parsedMessage.AppKey) ||
                    string.IsNullOrWhiteSpace(parsedMessage.Hwid) ||
                    string.IsNullOrWhiteSpace(parsedMessage.Msg))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parsedMessage.ID))
                {
                    parsedMessage.ID = Guid.NewGuid().ToString("N");
                }

                if (parsedMessage.MsgDate <= 0)
                {
                    parsedMessage.MsgDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() > int.MaxValue
                        ? int.MaxValue
                        : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                pushMessage = parsedMessage;
                return true;
            }
            catch (JsonException)
            {
                return false;
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
