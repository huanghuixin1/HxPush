using Microsoft.AspNetCore.StaticFiles;

namespace HxPushServerWeb
{
    // 应用入口：装配服务、中间件和 HTTP/WebSocket 路由。
    public class Program
    {
        // 初始化并运行 ASP.NET Core Web 服务。
        public static async Task Main(string[] args)
        {
            Console.WriteLine("当前版本： 1.0");
            var builder = WebApplication.CreateBuilder(args);

            // 默认监听所有网卡；局域网设备用本机 IP 访问，例如：
            // http://192.168.1.10:5212/ws-test.html
            if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            {
                builder.WebHost.UseUrls("http://0.0.0.0:5212");
            }

            // 数据库放在程序运行目录的 App_Data 下，启动时自动建库建表。
            var appDataPath = Path.Combine(AppContext.BaseDirectory, "App_Data");
            var databasePath = Path.Combine(appDataPath, "hxpush.db");
            var appKeyFilePath = Path.Combine(appDataPath, "appkeys.txt");
            var appKeyPasswordFilePath = Path.Combine(appDataPath, "appkey-password.txt");

            builder.Services.AddSingleton(new HxPushMessageRepository(databasePath));
            builder.Services.AddSingleton(new HxPushAppKeyManager(appKeyFilePath, appKeyPasswordFilePath));
            builder.Services.AddSingleton<HxPushHttpHandler>();
            builder.Services.AddSingleton<HxPushWebSocketHandler>();
            builder.Services.AddSingleton<HxPushMessageAdminHandler>();

            // 使用默认策略统一覆盖当前和后续新增的 HTTP 路由。
            builder.Services.AddCors(options =>
            {
                // 全局取消 HTTP 接口的跨域限制，允许任意来源、请求头和请求方法。
                options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod());
            });

            var app = builder.Build();

            // 初始化 SQLite，确保接口第一次被调用前表已经存在。
            await app.Services.GetRequiredService<HxPushMessageRepository>().InitializeAsync();

            // 中间件顺序保证静态文件和业务接口都能获得跨域响应头。
            // 默认静态文件不认 .apk 等扩展名会 404；补 MIME，未知类型按二进制下载。
            var contentTypeProvider = new FileExtensionContentTypeProvider();
            contentTypeProvider.Mappings[".apk"] = "application/vnd.android.package-archive";
            contentTypeProvider.Mappings[".aab"] = "application/octet-stream";

            app.UseCors();
            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = contentTypeProvider,
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });
            // Ping/Pong 心跳会主动清理强杀后未立即断开的连接，避免它长期留在在线客户端集合中。
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15),
                KeepAliveTimeout = TimeSpan.FromSeconds(10)
            });

            // Program 只负责路由转发，具体 HTTP/WebSocket 逻辑放到独立类。
            app.MapGet("/", (HxPushHttpHandler handler) => handler.HandleIndex());
            app.MapGet("/api/messages", (HttpRequest request, HxPushHttpHandler handler, CancellationToken cancellationToken) =>
                handler.HandleGetMessagesAsync(request, cancellationToken));
            app.MapGet("/api/messages/unread", (HttpRequest request, HxPushHttpHandler handler, CancellationToken cancellationToken) =>
                handler.HandleGetUnreadMessagesAsync(request, cancellationToken));
            app.MapPost("/api/messages", (HttpRequest request, HxPushHttpHandler handler, CancellationToken cancellationToken) =>
                handler.HandleCreateMessageAsync(request, cancellationToken));
            app.MapGet("/api/appkeys", (HttpRequest request, HxPushHttpHandler handler) =>
                handler.HandleGetAppKeys(request));
            app.MapPut("/api/appkeys", (HttpRequest request, HxPushHttpHandler handler, CancellationToken cancellationToken) =>
                handler.HandleReplaceAppKeysAsync(request, cancellationToken));
            // 消息管理端接口单独由 HxPushMessageAdminHandler 处理，与客户端消息 API 解耦。
            app.MapGet("/api/admin/messages", (HttpRequest request, HxPushMessageAdminHandler handler, CancellationToken cancellationToken) =>
                handler.HandleGetMessagesAsync(request, cancellationToken));
            app.MapDelete("/api/admin/messages", (HttpRequest request, HxPushMessageAdminHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteByIdsAsync(request, cancellationToken));
            app.MapDelete("/api/admin/messages/filter", (HttpRequest request, HxPushMessageAdminHandler handler, CancellationToken cancellationToken) =>
                handler.HandleDeleteByFilterAsync(request, cancellationToken));
            app.Map("/ws", async (HttpContext context, HxPushWebSocketHandler handler) =>
                await handler.HandleAsync(context));

            await app.RunAsync();
        }
    }
}
