using System.Net.WebSockets;
using HxPushApp.models.Message;
using HxPushServerWeb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

const string appKey = "ack-regression-app";
var testDirectory = Path.Combine(Path.GetTempPath(), $"hxpush-ack-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);

try
{
    var appKeyPath = Path.Combine(testDirectory, "appkeys.txt");
    var passwordPath = Path.Combine(testDirectory, "appkey-password.txt");
    var databasePath = Path.Combine(testDirectory, "hxpush.db");
    await File.WriteAllTextAsync(appKeyPath, appKey);
    await File.WriteAllTextAsync(passwordPath, "test-password");

    var repository = new HxPushMessageRepository(databasePath);
    await repository.InitializeAsync();

    var message = new HxPushMsgModel
    {
        ID = Guid.NewGuid().ToString("N"),
        AppKey = appKey,
        Hwid = "ack-regression-device",
        Msg = "客户端未确认时必须保持未读",
        IsRead = false
    };
    await repository.InsertAsync(message, CancellationToken.None);

    var appKeyManager = new HxPushAppKeyManager(appKeyPath, passwordPath);
    var handler = new HxPushWebSocketHandler(appKeyManager, repository);
    var socket = new SendThenDisconnectWebSocket();
    var context = new DefaultHttpContext();
    context.Request.QueryString = new QueryString($"?appkey={Uri.EscapeDataString(appKey)}");
    context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(socket));

    // 真实处理器完成“补发成功后客户端未回业务确认便断开”的完整路径。
    await handler.HandleAsync(context);

    var unreadMessages = await repository.GetUnreadAsync(appKey, null, CancellationToken.None);
    if (unreadMessages.All(item => item.ID != message.ID))
    {
        Console.Error.WriteLine(
            "RED: WebSocket 写入成功但客户端未确认，数据库消息却已被标记为已读。");
        return 1;
    }

    // 同一条消息在下一次连接时收到 ACK 后才允许变为已读。
    var acknowledgement = new HxPushDeliveryAckModel
    {
        MessageIds = [message.ID]
    };
    var acknowledgedSocket = new SendAcknowledgementThenDisconnectWebSocket(
        System.Text.Json.JsonSerializer.Serialize(acknowledgement));
    var acknowledgedContext = new DefaultHttpContext();
    acknowledgedContext.Request.QueryString = new QueryString($"?appkey={Uri.EscapeDataString(appKey)}");
    acknowledgedContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(acknowledgedSocket));
    await handler.HandleAsync(acknowledgedContext);

    unreadMessages = await repository.GetUnreadAsync(appKey, null, CancellationToken.None);
    if (unreadMessages.Any(item => item.ID == message.ID))
    {
        Console.Error.WriteLine("RED: 客户端已确认持久化，但数据库消息仍未标记为已读。");
        return 1;
    }

    Console.WriteLine("GREEN: 未 ACK 时保持未读，收到 ACK 后才标记已读。");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(testDirectory, recursive: true);
}

// 用可控 WebSocket 驱动服务端真实发送路径：发送成功，随后立即断线且绝不发送 ACK。
file sealed class SendThenDisconnectWebSocket : WebSocket
{
    private WebSocketState state = WebSocketState.Open;
    private WebSocketCloseStatus? closeStatus;
    private string? closeStatusDescription;

    public override WebSocketCloseStatus? CloseStatus => closeStatus;

    public override string? CloseStatusDescription => closeStatusDescription;

    public override WebSocketState State => state;

    public override string? SubProtocol => null;

    public override void Abort() => state = WebSocketState.Aborted;

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        this.closeStatus = closeStatus;
        closeStatusDescription = statusDescription;
        state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        state = WebSocketState.CloseReceived;
        return Task.FromResult(new WebSocketReceiveResult(
            0,
            WebSocketMessageType.Close,
            endOfMessage: true,
            WebSocketCloseStatus.NormalClosure,
            "simulated process termination"));
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class SendAcknowledgementThenDisconnectWebSocket(string acknowledgement) : WebSocket
{
    private WebSocketState state = WebSocketState.Open;
    private bool acknowledgementDelivered;
    private WebSocketCloseStatus? closeStatus;
    private string? closeStatusDescription;

    public override WebSocketCloseStatus? CloseStatus => closeStatus;

    public override string? CloseStatusDescription => closeStatusDescription;

    public override WebSocketState State => state;

    public override string? SubProtocol => null;

    public override void Abort() => state = WebSocketState.Aborted;

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        this.closeStatus = closeStatus;
        closeStatusDescription = statusDescription;
        state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!acknowledgementDelivered)
        {
            acknowledgementDelivered = true;
            var bytes = System.Text.Encoding.UTF8.GetBytes(acknowledgement);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(
                bytes.Length,
                WebSocketMessageType.Text,
                endOfMessage: true));
        }

        state = WebSocketState.CloseReceived;
        return Task.FromResult(new WebSocketReceiveResult(
            0,
            WebSocketMessageType.Close,
            endOfMessage: true,
            WebSocketCloseStatus.NormalClosure,
            "simulated graceful close"));
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class TestWebSocketFeature(WebSocket socket) : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest => true;

    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
}
