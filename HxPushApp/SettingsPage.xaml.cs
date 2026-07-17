using HxPushApp.Helpers;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;
using System.Text.Json;

namespace HxPushApp
{
    public partial class SettingsPage : ContentPage
    {
        private const int MaxWebSocketLogLines = 30;
        private static readonly ServerAddressOption[] ServerAddressOptions =
        [
            new("局域网", new Uri(AppSettings.DefaultServerAddress)),
            new("正式地址", new Uri("ws://43.142.82.217:5212/ws")),
            new("备用地址", new Uri("ws://push.huixingfifa.top:5212/ws"))
        ];

        private readonly Queue<string> webSocketLogLines = new();
        private readonly PushConnectionService pushConnectionService = PushConnectionService.Instance;
        private bool isSettingsToastVisible;
        private bool isServerPickerInitialized;

        public SettingsPage()
        {
            InitializeComponent();

            AppKeyEntry.Text = AppSettings.AppKeyInputValue;
            InitializeServerAddressPicker();

            pushConnectionService.LogMessage += (_, message) => AppendWebSocketLog(message);
            pushConnectionService.ConnectionStateChanged += (_, isConnected) =>
                UpdateWebSocketButtonStates(isConnected);

            UpdateWebSocketButtonStates(pushConnectionService.IsConnected);
        }

        /// <summary>
        /// 保存 AppKey 到 MAUI Preferences。空值不会覆盖已经保存的配置。
        /// </summary>
        private async void OnSaveAppKeyClicked(object? sender, EventArgs e)
        {
            await SaveAppKeyAsync("AppKey 已保存");
        }

        /// <summary>
        /// 用户在 AppKey 输入框按下完成键时自动保存。
        /// </summary>
        private async void OnAppKeyEntryCompleted(object? sender, EventArgs e)
        {
            await SaveAppKeyAsync("AppKey 已自动保存");
        }

        /// <summary>
        /// 校验并保存 AppKey；空值不会覆盖已经保存的配置。
        /// </summary>
        private async Task SaveAppKeyAsync(string successMessage)
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
                return;
            }

            _ = ConnectAfterAppKeySavedAsync();
            await ShowSettingsToastAsync(successMessage);
        }

        private async Task ConnectAfterAppKeySavedAsync()
        {
            try
            {
                await pushConnectionService.ConnectAsync();
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"自动连接失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 显示短暂的页面 Toast，避免为单个提示引入额外依赖。
        /// </summary>
        private async Task ShowSettingsToastAsync(string message)
        {
            if (isSettingsToastVisible)
            {
                return;
            }

            isSettingsToastVisible = true;
            try
            {
                SettingsToastLabel.Text = message;
                SettingsToast.IsVisible = true;
                await SettingsToast.FadeToAsync(1, 150);
                await Task.Delay(1500);
                await SettingsToast.FadeToAsync(0, 150);
            }
            finally
            {
                SettingsToast.IsVisible = false;
                isSettingsToastVisible = false;
            }
        }

        /// <summary>
        /// 初始化服务器选项，并恢复上次保存的地址。
        /// </summary>
        private void InitializeServerAddressPicker()
        {
            ServerAddressPicker.ItemsSource = ServerAddressOptions;

            var selectedOption = ServerAddressOptions.FirstOrDefault(
                option => string.Equals(
                    option.Uri.AbsoluteUri,
                    AppSettings.ServerAddress,
                    StringComparison.OrdinalIgnoreCase)) ?? ServerAddressOptions[0];

            AppSettings.ServerAddress = selectedOption.Uri.AbsoluteUri;
            ServerAddressPicker.SelectedItem = selectedOption;
            ServerAddressStatusLabel.Text = selectedOption.Uri.AbsoluteUri;
            isServerPickerInitialized = true;
        }

        /// <summary>
        /// 切换服务器后立即保存；已有连接会先断开，避免继续使用旧地址。
        /// </summary>
        private async void OnServerAddressSelected(object? sender, EventArgs e)
        {
            if (!isServerPickerInitialized ||
                ServerAddressPicker.SelectedItem is not ServerAddressOption selectedOption)
            {
                return;
            }

            AppSettings.ServerAddress = selectedOption.Uri.AbsoluteUri;
            ServerAddressStatusLabel.Text = selectedOption.Uri.AbsoluteUri;

            if (pushConnectionService.IsConnected)
            {
                await pushConnectionService.DisconnectAsync();
            }

            await ShowSettingsToastAsync($"已切换到{selectedOption.Name}");
        }

        /// <summary>
        /// 主动断开当前 WebSocket 测试连接。
        /// </summary>
        private async void OnWebSocketDisconnectClicked(object? sender, EventArgs e)
        {
            WebSocketDisconnectButton.IsEnabled = false;

            try
            {
                await pushConnectionService.DisconnectAsync();
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"断开失败：{ex.Message}");
            }
            finally
            {
                UpdateWebSocketButtonStates(pushConnectionService.IsConnected);
            }
        }

        private async void OnDeleteLocalCacheClicked(object? sender, EventArgs e)
        {
            var confirmed = await DisplayAlert(
                "删除本地缓存",
                "这会删除本机保存的全部消息，且无法恢复。",
                "删除",
                "取消");
            if (!confirmed)
            {
                return;
            }

            DeleteLocalCacheButton.IsEnabled = false;
            try
            {
                await SqliteHelper.Instance.DeleteDatabaseAsync();
                AppendWebSocketLog("本地消息缓存已删除。");
                await ShowSettingsToastAsync("本地缓存已删除");
            }
            catch (Exception ex)
            {
                AppendWebSocketLog($"删除本地缓存失败：{ex.Message}");
            }
            finally
            {
                DeleteLocalCacheButton.IsEnabled = true;
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
                await pushConnectionService.ConnectAsync();
                await pushConnectionService.SendTextAsync(payload);
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
        /// 使用已保存 AppKey 建立 WebSocket 连接并保持接收；成功后跳转到消息页。
        /// </summary>
        private async void OnWebSocketTestClicked(object? sender, EventArgs e)
        {
            WebSocketTestButton.IsEnabled = false;

            try
            {
                // AppKey 随握手 URL 提交，连接成功即表示服务端校验通过。
                await pushConnectionService.ConnectAsync();
                AppendWebSocketLog("AppKey 校验通过，连接已保持，正在持续接收服务端消息。");

                if (pushConnectionService.IsConnected)
                {
                    var navigationParameters = new ShellNavigationQueryParameters
                    {
                        [MessagesPage.ConnectionSuccessToastQueryKey] = true
                    };
                    await Shell.Current.GoToAsync("//MessagesPage", navigationParameters);
                    return;
                }
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
                UpdateWebSocketButtonStates(pushConnectionService.IsConnected);
            }
        }

        /// <summary>
        /// 根据连接状态切换开始连接和断开连接按钮。
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
            return new HxPushMsgModel
            {
                ID = Guid.NewGuid().ToString("N"),
                AppKey = AppSettings.AppKey,
                Hwid = AppSettings.DeviceId,
                Msg = message
            };
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

    public sealed record ServerAddressOption(string Name, Uri Uri);
}
