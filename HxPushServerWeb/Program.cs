namespace HxPushServerWeb
{
    // 应用入口：装配服务、中间件和 HTTP/WebSocket 路由。
    public class Program
    {
        // 初始化并运行 ASP.NET Core Web 服务。
        public static async Task Main(string[] args)
        {
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
            app.UseCors();
            app.UseStaticFiles();
            app.UseWebSockets();

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
            app.Map("/ws", async (HttpContext context, HxPushWebSocketHandler handler) =>
                await handler.HandleAsync(context));

            await app.RunAsync();
        }
    }
}
