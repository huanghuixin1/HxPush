using System.Text.Json;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 应用级推送连接服务。统一持有一条 WebSocket 连接，并负责将收到的推送写入 SQLite。
    /// </summary>
    public sealed class PushConnectionService
    {
        private static readonly Lazy<PushConnectionService> LazyInstance =
            new(() => new PushConnectionService());

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly WebSocketClientHelper webSocketClient = new();
        private readonly SqliteHelper sqliteHelper = SqliteHelper.Instance;
        private readonly SemaphoreSlim connectionLock = new(1, 1);

        private PushConnectionService()
        {
            webSocketClient.StatusChanged += (_, message) => RaiseLogMessage(message);
            webSocketClient.ConnectionStateChanged += (_, isConnected) =>
                ConnectionStateChanged?.Invoke(this, isConnected);
            webSocketClient.TextMessageReceived += async (_, message) =>
                await HandleTextMessageAsync(message);
            webSocketClient.BinaryMessageReceived += (_, length) =>
                RaiseLogMessage($"接收：{length} 字节二进制消息。");
        }

        public static PushConnectionService Instance => LazyInstance.Value;

        public event EventHandler<string>? LogMessage;

        public event EventHandler<bool>? ConnectionStateChanged;

        public bool IsConnected => webSocketClient.IsConnected;

        /// <summary>
        /// 使用当前保存的服务器地址和 AppKey 建立连接。
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var appKey = AppSettings.AppKey.Trim();
            if (string.IsNullOrWhiteSpace(appKey))
            {
                throw new InvalidOperationException("请先保存 AppKey。");
            }

            if (!Uri.TryCreate(AppSettings.ServerAddress, UriKind.Absolute, out var serverUri))
            {
                throw new InvalidOperationException("已保存的服务器地址无效。");
            }

            await connectionLock.WaitAsync(cancellationToken);
            try
            {
                await webSocketClient.ConnectAsync(serverUri, appKey, cancellationToken);
            }
            finally
            {
                connectionLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await connectionLock.WaitAsync();
            try
            {
                await webSocketClient.DisconnectAsync();
            }
            finally
            {
                connectionLock.Release();
            }
        }

        public Task SendTextAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            return webSocketClient.SendTextAsync(message, cancellationToken);
        }

        private async Task HandleTextMessageAsync(string message)
        {
            RaiseLogMessage($"接收：{message}");

            if (!TryParsePushMessage(message, out var pushMessage))
            {
                return;
            }

            try
            {
                await sqliteHelper.SaveMessageAsync(pushMessage);
                RaiseLogMessage($"已保存消息：{pushMessage.ID}");
            }
            catch (Exception ex)
            {
                RaiseLogMessage($"保存消息失败：{ex.Message}");
            }
        }

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
                var parsedMessage = JsonSerializer.Deserialize<HxPushMsgModel>(message, JsonOptions);
                if (parsedMessage is null ||
                    string.IsNullOrWhiteSpace(parsedMessage.AppKey) ||
                    string.IsNullOrWhiteSpace(parsedMessage.Hwid) ||
                    string.IsNullOrWhiteSpace(parsedMessage.Msg) ||
                    parsedMessage.MsgDate <= 0)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parsedMessage.ID))
                {
                    parsedMessage.ID = Guid.NewGuid().ToString("N");
                }

                pushMessage = parsedMessage;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void RaiseLogMessage(string message)
        {
            LogMessage?.Invoke(this, message);
        }
    }
}
