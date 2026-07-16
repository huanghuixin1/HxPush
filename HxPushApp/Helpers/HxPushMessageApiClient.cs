using HxPushApp.models.Message;
using HxPushSdk;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 应用侧消息拉取入口：只负责 AppSettings 配置与超时，HTTP 实现全部委托 HxPushSdk.HxPushWebApiClient。
    /// 不写 SQLite，保持网络与本地存储解耦。
    /// </summary>
    public sealed class HxPushMessageApiClient
    {
        private const int MaxPageSize = 50;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly Lazy<HxPushMessageApiClient> LazyInstance =
            new(() => new HxPushMessageApiClient());

        private readonly object clientSync = new();
        private HxPushWebApiClient? webApiClient;
        private string? boundServerAddress;

        private HxPushMessageApiClient()
        {
        }

        public static HxPushMessageApiClient Instance => LazyInstance.Value;

        /// <summary>
        /// 拉取服务端最新消息，或按 MsgDate + ID 游标继续拉取更旧消息。
        /// 单次请求最多等待 10 秒，超时由调用方转为页面提示。
        /// </summary>
        public async Task<IReadOnlyList<HxPushMsgModel>> GetMessagesAsync(
            int pageSize = MaxPageSize,
            string? hwid = null,
            HxPushMessageCursor? before = null,
            CancellationToken cancellationToken = default)
        {
            if (!AppSettings.HasAppKey)
            {
                return Array.Empty<HxPushMsgModel>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var client = GetOrCreateClient();
            return await client.GetMessagesAsync(
                    AppSettings.AppKey,
                    pageIndex: 1,
                    pageSize: Math.Clamp(pageSize, 1, MaxPageSize),
                    hwid: hwid,
                    before: before,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 按当前 ServerAddress 复用 SDK 客户端；地址变更时丢弃旧实例并重建。
        /// </summary>
        private HxPushWebApiClient GetOrCreateClient()
        {
            var serverAddress = AppSettings.ServerAddress;
            lock (clientSync)
            {
                if (webApiClient is not null
                    && string.Equals(boundServerAddress, serverAddress, StringComparison.Ordinal))
                {
                    return webApiClient;
                }

                webApiClient?.Dispose();
                webApiClient = new HxPushWebApiClient(serverAddress);
                boundServerAddress = serverAddress;
                return webApiClient;
            }
        }
    }
}
