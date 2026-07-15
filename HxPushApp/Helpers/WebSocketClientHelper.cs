using System.Net.WebSockets;
using System.Text;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// WebSocket 客户端帮助类，负责携带 AppKey 连接、发送、持续接收和关闭连接。
    /// 页面只需要订阅事件并调用公开方法，不直接处理底层 ClientWebSocket。
    /// </summary>
    public sealed class WebSocketClientHelper : IAsyncDisposable
    {
        private readonly Uri serverUri;
        private readonly TimeSpan operationTimeout;
        private readonly SemaphoreSlim sendLock = new(1, 1);

        private ClientWebSocket? webSocket;
        private CancellationTokenSource? receiveCts;
        private Task? receiveTask;
        private string? connectedAppKey;

        /// <summary>
        /// 创建 WebSocket 客户端帮助类。
        /// </summary>
        /// <param name="serverUri">WebSocket 服务地址。</param>
        /// <param name="operationTimeout">连接和发送的超时时间，默认 10 秒。</param>
        public WebSocketClientHelper(Uri serverUri, TimeSpan? operationTimeout = null)
        {
            this.serverUri = serverUri;
            this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// 收到文本消息时触发。
        /// </summary>
        public event EventHandler<string>? TextMessageReceived;

        /// <summary>
        /// 收到二进制消息时触发，参数表示消息字节数。
        /// </summary>
        public event EventHandler<long>? BinaryMessageReceived;

        /// <summary>
        /// 连接状态变化或异常提示时触发。
        /// </summary>
        public event EventHandler<string>? StatusChanged;

        /// <summary>
        /// 连接状态变化时触发；true 表示已连接，false 表示已断开。
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 当前 WebSocket 是否处于已连接状态。
        /// </summary>
        public bool IsConnected => webSocket?.State == WebSocketState.Open;

        /// <summary>
        /// 使用 AppKey 连接 WebSocket 服务；连接成功后会自动启动后台接收循环。
        /// </summary>
        /// <param name="appKey">用于服务端握手校验的 AppKey。</param>
        /// <param name="cancellationToken">取消连接操作的令牌。</param>
        public async Task ConnectAsync(
            string appKey,
            CancellationToken cancellationToken = default)
        {
            var normalizedAppKey = appKey?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedAppKey))
            {
                throw new ArgumentException("AppKey 不能为空。", nameof(appKey));
            }

            if (IsConnected && string.Equals(connectedAppKey, normalizedAppKey, StringComparison.Ordinal))
            {
                return;
            }

            // AppKey 改变时先断开旧身份，避免复用到不匹配的连接。
            await DisconnectAsync();

            var socket = new ClientWebSocket();
            var connectionUri = BuildConnectionUri(normalizedAppKey);
            try
            {
                using var timeout = CreateOperationTimeout(cancellationToken);
                await socket.ConnectAsync(connectionUri, timeout.Token);

                webSocket = socket;
                connectedAppKey = normalizedAppKey;
                receiveCts = new CancellationTokenSource();

                RaiseConnectionStateChanged(isConnected: true);

                // 后台持续接收服务端推送，避免阻塞 UI 线程。
                receiveTask = Task.Run(
                    () => ReceiveMessagesAsync(socket, receiveCts.Token),
                    receiveCts.Token);

                // 日志不输出查询参数，避免 AppKey 被无意复制或截图传播。
                RaiseStatusChanged($"已连接：{serverUri}（AppKey 已校验）");
            }
            catch
            {
                socket.Dispose();
                connectedAppKey = null;
                RaiseConnectionStateChanged(isConnected: false);
                throw;
            }
        }

        /// <summary>
        /// 发送一条 UTF-8 文本消息。
        /// </summary>
        public async Task SendTextAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            if (webSocket?.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket 尚未连接。");
            }

            var messageBytes = Encoding.UTF8.GetBytes(message);

            // ClientWebSocket 不建议并发 Send，这里用锁保证同一时间只发送一条消息。
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CreateOperationTimeout(cancellationToken);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    timeout.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        /// <summary>
        /// 取消接收循环并关闭当前 WebSocket 连接。
        /// </summary>
        public async Task DisconnectAsync()
        {
            var socket = webSocket;
            var currentReceiveCts = receiveCts;
            var currentReceiveTask = receiveTask;
            var hadConnection = socket is not null;

            webSocket = null;
            receiveCts = null;
            receiveTask = null;
            connectedAppKey = null;

            try
            {
                currentReceiveCts?.Cancel();

                if (socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client closed",
                        closeTimeout.Token);
                }

                if (currentReceiveTask is not null)
                {
                    await currentReceiveTask;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                currentReceiveCts?.Dispose();
                socket?.Dispose();

                if (hadConnection)
                {
                    RaiseConnectionStateChanged(isConnected: false);
                    RaiseStatusChanged("连接已断开。");
                }
            }
        }

        /// <summary>
        /// 释放帮助类内部资源。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            sendLock.Dispose();
        }

        /// <summary>
        /// 持续接收服务端消息，支持 WebSocket 分片消息合并。
        /// </summary>
        private async Task ReceiveMessagesAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];

            try
            {
                while (!cancellationToken.IsCancellationRequested
                       && socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    WebSocketMessageType? messageType = null;

                    do
                    {
                        result = await socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            RaiseStatusChanged(GetCloseMessage(result));
                            await AcknowledgeCloseAsync(socket);
                            return;
                        }

                        messageType ??= result.MessageType;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (messageType == WebSocketMessageType.Text)
                    {
                        var receivedText = Encoding.UTF8.GetString(message.ToArray());
                        TextMessageReceived?.Invoke(this, receivedText);
                    }
                    else
                    {
                        BinaryMessageReceived?.Invoke(this, message.Length);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                RaiseStatusChanged($"接收失败：{ex.Message}");
            }
            finally
            {
                RaiseConnectionStateChanged(isConnected: false);
            }
        }

        /// <summary>
        /// 创建带统一超时时间的取消令牌。
        /// </summary>
        private CancellationTokenSource CreateOperationTimeout(
            CancellationToken cancellationToken)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operationTimeout);
            return timeout;
        }

        /// <summary>
        /// 在基础地址上追加经过 URL 编码的 AppKey 查询参数。
        /// </summary>
        private Uri BuildConnectionUri(string appKey)
        {
            var separator = string.IsNullOrEmpty(serverUri.Query) ? "?" : "&";
            return new Uri($"{serverUri}{separator}appkey={Uri.EscapeDataString(appKey)}");
        }

        /// <summary>
        /// 生成服务端关闭连接时的提示信息。
        /// </summary>
        private static string GetCloseMessage(WebSocketReceiveResult result)
        {
            var reason = result.CloseStatusDescription;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = result.CloseStatus?.ToString() ?? "未知原因";
            }

            return $"服务端关闭连接：{reason}";
        }

        /// <summary>
        /// 服务端主动关闭时，客户端回应关闭握手。
        /// </summary>
        private static async Task AcknowledgeCloseAsync(ClientWebSocket socket)
        {
            if (socket.State != WebSocketState.CloseReceived)
            {
                return;
            }

            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Close acknowledged",
                closeTimeout.Token);
        }

        /// <summary>
        /// 统一抛出状态提示事件。
        /// </summary>
        private void RaiseStatusChanged(string message)
        {
            StatusChanged?.Invoke(this, message);
        }

        /// <summary>
        /// 统一抛出连接状态事件。
        /// </summary>
        private void RaiseConnectionStateChanged(bool isConnected)
        {
            ConnectionStateChanged?.Invoke(this, isConnected);
        }
    }
}
