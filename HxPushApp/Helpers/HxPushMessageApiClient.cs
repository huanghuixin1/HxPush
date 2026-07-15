using System.Text.Json;
using HxPushApp.models.Message;
using HxPushModel.HttpRequest;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 远端消息 HTTP 客户端，只负责从服务端拉取消息。
    /// 不直接写 SQLite，避免网络请求和本地存储耦合在一起。
    /// </summary>
    public sealed class HxPushMessageApiClient
    {
        private const int MaxPageSize = 50;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly Lazy<HxPushMessageApiClient> LazyInstance =
            new(() => new HxPushMessageApiClient());

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClientHelper httpClientHelper = HttpClientHelper.Instance;

        private HxPushMessageApiClient()
        {
        }

        public static HxPushMessageApiClient Instance => LazyInstance.Value;

        /// <summary>
        /// 拉取服务端最新消息，或按 MsgDate + ID 游标继续拉取更旧消息。
        /// 单次请求最多等待 10 秒，超时由调用方转换为页面提示。
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

            var requestUri = BuildMessagesRequestUri(
                Math.Clamp(pageSize, 1, MaxPageSize),
                hwid,
                before);
            var response = await httpClientHelper.GetJsonAsync<HxHttpResModel>(
                requestUri,
                timeout.Token).ConfigureAwait(false);

            if (response.code != 0)
            {
                throw new InvalidOperationException(response.msg?.ToString() ?? "服务端返回失败。");
            }

            return DeserializeMessages(response.msg);
        }

        private static string BuildMessagesRequestUri(
            int pageSize,
            string? hwid,
            HxPushMessageCursor? before)
        {
            var parameters = new List<string>
            {
                $"appkey={Uri.EscapeDataString(AppSettings.AppKey)}",
                "pageindex=1",
                $"pagesize={pageSize}"
            };

            if (!string.IsNullOrWhiteSpace(hwid))
            {
                parameters.Add($"hwid={Uri.EscapeDataString(hwid)}");
            }

            if (before is not null)
            {
                parameters.Add($"beforemsgdate={before.MsgDate}");
                parameters.Add($"beforeid={Uri.EscapeDataString(before.ID)}");
            }

            return $"{GetHttpBaseAddress()}/api/messages?{string.Join("&", parameters)}";
        }

        /// <summary>
        /// 设置页保存的是 WebSocket 地址，这里转换为同服务的 HTTP Base URL。
        /// </summary>
        private static string GetHttpBaseAddress()
        {
            var serverUri = new Uri(AppSettings.ServerAddress);
            var scheme = serverUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp;

            return $"{scheme}://{serverUri.Authority}";
        }

        private static IReadOnlyList<HxPushMsgModel> DeserializeMessages(object? value)
        {
            if (value is JsonElement element)
            {
                return element.Deserialize<List<HxPushMsgModel>>(JsonOptions)
                    ?? new List<HxPushMsgModel>();
            }

            var json = JsonSerializer.Serialize(value, JsonOptions);
            return JsonSerializer.Deserialize<List<HxPushMsgModel>>(json, JsonOptions)
                ?? new List<HxPushMsgModel>();
        }
    }

    /// <summary>
    /// 消息分页游标。排序规则与本地和服务端统一：MsgDate DESC, ID DESC。
    /// </summary>
    public sealed record HxPushMessageCursor(long MsgDate, string ID);
}
