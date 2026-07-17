using System.Text.Json;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 应用级推送连接服务。统一持有一条 WebSocket 连接，并负责将收到的推送写入 SQLite。
    /// 支持：意外断开后自动重连；应用从后台/锁屏恢复时 EnsureConnected 补连。
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
        private readonly object reconnectSync = new();

        /// <summary>为 true 时表示用户期望保持在线（手动连接成功或启动连接成功）。</summary>
        private bool maintainConnection;
        private int reconnectAttempt;
        private CancellationTokenSource? reconnectCts;
        private int ensureInProgress;

        private PushConnectionService()
        {
            webSocketClient.StatusChanged += (_, message) => RaiseLogMessage(message);
            webSocketClient.ConnectionStateChanged += OnSocketConnectionStateChanged;
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

        /// <summary>是否处于“应保持连接”模式（断开后会尝试自动重连）。</summary>
        public bool MaintainConnection => maintainConnection;

        /// <summary>
        /// 使用当前保存的服务器地址和 AppKey 建立连接。
        /// 成功后开启 maintain，意外断开或回到前台时会自动补连。
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
                maintainConnection = true;
                reconnectAttempt = 0;
                CancelScheduledReconnect();
            }
            finally
            {
                connectionLock.Release();
            }
        }

        /// <summary>
        /// 主动断开，并关闭自动重连（用户点击“断开连接”时使用）。
        /// </summary>
        public async Task DisconnectAsync()
        {
            maintainConnection = false;
            CancelScheduledReconnect();

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

        /// <summary>
        /// 应用恢复前台/解锁后调用：若应保持连接且当前已断开，则立即尝试重连。
        /// </summary>
        public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            if (!maintainConnection || !AppSettings.HasAppKey || IsConnected)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref ensureInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                RaiseLogMessage("检测到连接已断开，正在自动重连…");
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                RaiseLogMessage("自动重连成功。");
            }
            catch (Exception ex)
            {
                RaiseLogMessage($"自动重连失败：{ex.Message}");
                ScheduleReconnect();
            }
            finally
            {
                Interlocked.Exchange(ref ensureInProgress, 0);
            }
        }

        public Task SendTextAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            return webSocketClient.SendTextAsync(message, cancellationToken);
        }

        private void OnSocketConnectionStateChanged(object? sender, bool isConnected)
        {
            ConnectionStateChanged?.Invoke(this, isConnected);

            if (isConnected)
            {
                reconnectAttempt = 0;
                CancelScheduledReconnect();
                return;
            }

            // 意外断开（锁屏/休眠/网络切换等）且仍需在线时，安排退避重连。
            if (maintainConnection)
            {
                RaiseLogMessage("连接已断开，将自动尝试重连。");
                ScheduleReconnect();
            }
        }

        private void ScheduleReconnect()
        {
            if (!maintainConnection || !AppSettings.HasAppKey)
            {
                return;
            }

            CancellationToken token;
            lock (reconnectSync)
            {
                reconnectCts?.Cancel();
                reconnectCts?.Dispose();
                reconnectCts = new CancellationTokenSource();
                token = reconnectCts.Token;
            }

            var attempt = Interlocked.Increment(ref reconnectAttempt);
            // 1s, 2s, 4s … 上限 30s，避免锁屏后疯狂重试。
            var delaySeconds = Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5)));

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || !maintainConnection || IsConnected)
                    {
                        return;
                    }

                    await EnsureConnectedAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RaiseLogMessage($"计划重连异常：{ex.Message}");
                }
            }, token);
        }

        private void CancelScheduledReconnect()
        {
            lock (reconnectSync)
            {
                reconnectCts?.Cancel();
                reconnectCts?.Dispose();
                reconnectCts = null;
            }
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
