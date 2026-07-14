namespace HxPushServerWeb
{
    public class Program
    {
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

            builder.Services.AddSingleton(new HxPushMessageRepository(databasePath));
            builder.Services.AddSingleton(new HxPushAppKeyManager(appKeyFilePath));
            builder.Services.AddSingleton<HxPushHttpHandler>();
            builder.Services.AddSingleton<HxPushWebSocketHandler>();

            var app = builder.Build();

            // 初始化 SQLite，确保接口第一次被调用前表已经存在。
            await app.Services.GetRequiredService<HxPushMessageRepository>().InitializeAsync();

            app.UseStaticFiles();
            app.UseWebSockets();

            // Program 只负责路由转发，具体 HTTP/WebSocket 逻辑放到独立类。
            app.MapGet("/", (HxPushHttpHandler handler) => handler.HandleIndex());
            app.MapPost("/api/messages", (HttpRequest request, HxPushHttpHandler handler, CancellationToken cancellationToken) =>
                handler.HandleCreateMessageAsync(request, cancellationToken));
            app.Map("/ws", async (HttpContext context, HxPushWebSocketHandler handler) =>
                await handler.HandleAsync(context));

            await app.RunAsync();
        }
    }
}
