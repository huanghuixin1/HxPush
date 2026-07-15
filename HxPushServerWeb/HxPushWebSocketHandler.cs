using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HxPushApp.models.Message;

namespace HxPushServerWeb
{
    // 负责 /ws：握手校验 AppKey、接收消息，并支持 HTTP 推送复用已登记连接。
    internal sealed class HxPushWebSocketHandler
    {
        // 客户端集合允许连接处理与 HTTP 推送并发访问。
        private readonly HxPushAppKeyManager appKeyManager;
        private readonly HxPushMessageRepository messageRepository;
        private readonly ConcurrentDictionary<Guid, WebSocketClient> clients = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> unreadDeliveryLocks = new(StringComparer.Ordinal);

        // 收发消息共用序列化配置，兼容属性名大小写并保留中文。
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        // 注入 AppKey 白名单管理器。
        public HxPushWebSocketHandler(
            HxPushAppKeyManager appKeyManager,
            HxPushMessageRepository messageRepository)
        {
            this.appKeyManager = appKeyManager;
            this.messageRepository = messageRepository;
        }

        // 校验并接受 WebSocket 请求，在连接生命周期内维护客户端登记信息。
        public async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                // 普通 HTTP 请求仍按项目约定返回统一 JSON。
                await HxPushHttpHandler.ToJsonResult(
                    HxPushHttpHandler.Error("Please connect with WebSocket.")).ExecuteAsync(context);
                return;
            }

            // 在协议升级前校验查询参数，失败时客户端不会建立 WebSocket 连接。
            var appKey = context.Request.Query["appkey"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(appKey) || !appKeyManager.Exists(appKey))
            {
                await HxPushHttpHandler.ToJsonResult(
                    HxPushHttpHandler.Error("AppKey 不存在或无效。"),
                    StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new WebSocketClient(Guid.NewGuid(), appKey, webSocket);
            clients[client.Id] = client;

            Console.WriteLine($"WebSocket 客户端连接：client={client.Id}, appKey={client.AppKey}");

            try
            {
                // 新连接先补发 AppKey 下的离线未读列表，再进入实时接收循环。
                await SendUnreadMessagesAsync(client, context.RequestAborted);
                await ReceiveLoopAsync(client, context.RequestAborted);
            }
            finally
            {
                // 无论正常关闭还是异常退出，都及时移除连接。
                clients.TryRemove(client.Id, out _);
                Console.WriteLine($"WebSocket 客户端断开：{client.Id}");
            }
        }

        // 将消息推送给所有登记了相同 AppKey 的在线连接。
        public async Task<int> SendToAppKeyAsync(HxPushMsgModel message, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var sentCount = 0;

            foreach (var client in clients.Values)
            {
                if (!string.Equals(client.AppKey, message.AppKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (client.WebSocket.State != WebSocketState.Open)
                {
                    // 顺便清理已失效但尚未退出接收循环的连接。
                    clients.TryRemove(client.Id, out _);
                    continue;
                }

                try
                {
                    if (await SendTextAsync(client, json, cancellationToken))
                    {
                        sentCount++;
                    }
                    else
                    {
                        // 等待发送锁期间连接可能关闭，此时不能计入成功数。
                        clients.TryRemove(client.Id, out _);
                    }
                }
                catch (WebSocketException)
                {
                    // 单个客户端发送失败不应中断其他客户端推送。
                    clients.TryRemove(client.Id, out _);
                }
            }

            return sentCount;
        }

        // 将全部未读消息作为一个 JSON 数组发给新连接，成功后才批量标记已读。
        private async Task SendUnreadMessagesAsync(
            WebSocketClient client,
            CancellationToken cancellationToken)
        {
            // 同一 AppKey 的并发连接串行领取未读列表，避免重复补发同一批消息。
            var deliveryLock = unreadDeliveryLocks.GetOrAdd(
                client.AppKey,
                _ => new SemaphoreSlim(1, 1));
            await deliveryLock.WaitAsync(cancellationToken);

            try
            {
                var unreadMessages = await messageRepository.GetUnreadAsync(
                    client.AppKey,
                    hwid: null,
                    cancellationToken);
                if (unreadMessages.Count == 0)
                {
                    return;
                }

                // 当前发送即完成投递，客户端收到的模型与随后写入的数据库状态保持一致。
                foreach (var message in unreadMessages)
                {
                    message.IsRead = true;
                }

                var json = JsonSerializer.Serialize(unreadMessages, JsonOptions);
                if (!await SendTextAsync(client, json, cancellationToken))
                {
                    // 未真正写入 WebSocket 时保留未读，供下次连接继续补发。
                    return;
                }

                await messageRepository.MarkAsReadAsync(
                    unreadMessages.Select(message => message.ID).ToArray(),
                    cancellationToken);

                Console.WriteLine(
                    $"WebSocket 未读消息补发：client={client.Id}, appKey={client.AppKey}, count={unreadMessages.Count}");
            }
            finally
            {
                deliveryLock.Release();
            }
        }

        // 持续接收客户端消息，直到连接关闭或数据不合法。
        private async Task ReceiveLoopAsync(WebSocketClient client, CancellationToken cancellationToken)
        {
            while (client.WebSocket.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(client.WebSocket, cancellationToken);

                if (text is null)
                {
                    break;
                }

                HxPushMsgModel? message;

                try
                {
                    message = JsonSerializer.Deserialize<HxPushMsgModel>(text, JsonOptions);
                }
                catch (JsonException)
                {
                    // 无法解析时使用标准关闭码告知客户端载荷错误。
                    await CloseAsync(client.WebSocket, WebSocketCloseStatus.InvalidPayloadData, "invalid HxPushMsgModel json", cancellationToken);
                    break;
                }

                if (message is null ||
                    string.IsNullOrWhiteSpace(message.AppKey) ||
                    string.IsNullOrWhiteSpace(message.Hwid) ||
                    string.IsNullOrWhiteSpace(message.Msg))
                {
                    await CloseAsync(client.WebSocket, WebSocketCloseStatus.InvalidPayloadData, "AppKey, Hwid and Msg are required", cancellationToken);
                    break;
                }

                if (!string.Equals(message.AppKey.Trim(), client.AppKey, StringComparison.Ordinal) ||
                    !appKeyManager.Exists(client.AppKey))
                {
                    // 消息不能切换握手时已确认的 AppKey，白名单撤销后也会关闭旧连接。
                    await CloseAsync(client.WebSocket, WebSocketCloseStatus.PolicyViolation, "AppKey does not match connection", cancellationToken);
                    break;
                }

                // 统一连接身份和缺省字段后，转发给同 AppKey 的全部连接。
                message.AppKey = client.AppKey;
                client.Hwid = message.Hwid.Trim();
                message.Hwid = client.Hwid;

                if (string.IsNullOrWhiteSpace(message.ID))
                {
                    message.ID = Guid.NewGuid().ToString("N");
                }

                // 无条件覆盖客户端时间，所有 WS 推送也使用服务端 MsgDate。
                message.MsgDate = messageRepository.CreateMessageTimestamp();

                // WS 入站消息会立即尝试推送，不进入离线消息表。
                message.IsRead = true;
                var pushCount = await SendToAppKeyAsync(message, cancellationToken);

                Console.WriteLine($"WebSocket 消息推送：client={client.Id}, appKey={client.AppKey}, pushed={pushCount}");
            }
        }

        // 合并 WebSocket 分片并只接受 UTF-8 文本消息。
        private static async Task<string?> ReceiveTextAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 4];
            using var message = new MemoryStream();

            while (true)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // 对客户端关闭请求完成标准关闭握手。
                    await CloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "closed by client", cancellationToken);
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "text message only", cancellationToken);
                    return null;
                }

                message.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        // 串行发送一条完整文本消息。
        private static async Task<bool> SendTextAsync(
            WebSocketClient client,
            string text,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            // 同一个 WebSocket 不能并发 SendAsync，所以每个客户端有自己的发送锁。
            await client.SendLock.WaitAsync(cancellationToken);
            try
            {
                if (client.WebSocket.State == WebSocketState.Open)
                {
                    await client.WebSocket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        WebSocketMessageFlags.EndOfMessage,
                        cancellationToken);
                    return true;
                }

                return false;
            }
            finally
            {
                client.SendLock.Release();
            }
        }

        // 仅在允许发起关闭握手的状态下关闭连接。
        private static async Task CloseAsync(
            WebSocket webSocket,
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken)
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);
            }
        }

        // 保存单个客户端的连接、登记信息和发送锁。
        private sealed class WebSocketClient
        {
            // 绑定连接唯一标识、已校验 AppKey 与底层 WebSocket。
            public WebSocketClient(Guid id, string appKey, WebSocket webSocket)
            {
                Id = id;
                AppKey = appKey;
                WebSocket = webSocket;
            }

            // 连接标识用于并发字典增删。
            public Guid Id { get; }

            // 当前客户端的底层 WebSocket。
            public WebSocket WebSocket { get; }

            // 防止同一连接发生并发发送。
            public SemaphoreSlim SendLock { get; } = new(1, 1);

            // 握手阶段确认后，连接生命周期内不允许切换 AppKey。
            public string AppKey { get; }

            // 首条合法业务消息登记的设备标识。
            public string Hwid { get; set; } = string.Empty;
        }
    }
}
