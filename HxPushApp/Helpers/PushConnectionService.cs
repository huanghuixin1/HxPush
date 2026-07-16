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

        /// <summary>
        /// Raised after received push messages have been saved to the local database.
        /// </summary>
        public event EventHandler<IReadOnlyList<HxPushMsgModel>>? PushMessagesReceived;

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

            if (!TryParsePushMessages(message, out var pushMessages))
            {
                return;
            }

            try
            {
                await sqliteHelper.SaveMessagesAsync(pushMessages);
                PushMessagesReceived?.Invoke(this, pushMessages);
                RaiseLogMessage($"已保存消息：{pushMessages.Count} 条");

                // SQLite 写入成功才确认投递；强杀或断网导致 ACK 未送达时，服务端会在下次连接后重发。
                var acknowledgement = new HxPushDeliveryAckModel
                {
                    MessageIds = pushMessages
                        .Select(pushMessage => pushMessage.ID)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
                await webSocketClient.SendTextAsync(
                    JsonSerializer.Serialize(acknowledgement, JsonOptions));
            }
            catch (Exception ex)
            {
                RaiseLogMessage($"保存消息或发送 ACK 失败：{ex.Message}");
            }
        }

        // 同时兼容实时单条对象和连接后补发的消息数组。
        private static bool TryParsePushMessages(
            string message,
            out IReadOnlyList<HxPushMsgModel> pushMessages)
        {
            pushMessages = Array.Empty<HxPushMsgModel>();

            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                var trimmedMessage = message.TrimStart();
                HxPushMsgModel[] parsedMessages;

                if (trimmedMessage.StartsWith('['))
                {
                    parsedMessages = JsonSerializer.Deserialize<HxPushMsgModel[]>(message, JsonOptions)
                        ?? Array.Empty<HxPushMsgModel>();
                }
                else if (trimmedMessage.StartsWith('{'))
                {
                    var parsedMessage = JsonSerializer.Deserialize<HxPushMsgModel>(message, JsonOptions);
                    parsedMessages = parsedMessage is null ? Array.Empty<HxPushMsgModel>() : new[] { parsedMessage };
                }
                else
                {
                    return false;
                }

                // 整批验证核心字段，避免保存不完整的服务端载荷。
                foreach (var parsedMessage in parsedMessages)
                {
                    if (string.IsNullOrWhiteSpace(parsedMessage.AppKey) ||
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
                }

                pushMessages = parsedMessages;
                return parsedMessages.Length > 0;
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
