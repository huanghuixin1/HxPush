using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HxPushModel.HttpRequest;

namespace HxPushServerWeb
{
    // 消息管理端 HTTP 接口：密码鉴权、只读分页查询、按 ID/筛选条件删除。
    // 与客户端 /api/messages 分离，查询不会把未读标记为已读。
    internal sealed class HxPushMessageAdminHandler
    {
        private const string ManagerPasswordHeader = "X-AppKey-Manager-Password";
        private const int MaxPageSize = 100;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        private readonly HxPushMessageRepository messageRepository;
        private readonly HxPushAppKeyManager appKeyManager;

        public HxPushMessageAdminHandler(
            HxPushMessageRepository messageRepository,
            HxPushAppKeyManager appKeyManager)
        {
            this.messageRepository = messageRepository;
            this.appKeyManager = appKeyManager;
        }

        // GET /api/admin/messages：管理端分页列表，不改 IsRead。
        public async Task<IResult> HandleGetMessagesAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            DisableResponseCaching(request);

            if (!IsManagerAuthorized(request))
            {
                return ToJsonResult(Error("管理密码不正确。"), StatusCodes.Status403Forbidden);
            }

            if (!TryParsePage(request, out var pageIndex, out var pageSize, out var pageError))
            {
                return ToJsonResult(Error(pageError));
            }

            var appKey = request.Query["appkey"].ToString().Trim();
            var hwid = request.Query["hwid"].ToString().Trim();
            var keyword = request.Query["keyword"].ToString().Trim();
            if (!TryParseOptionalIsRead(request, out var isRead, out var isReadError))
            {
                return ToJsonResult(Error(isReadError));
            }

            if (!TryParseSortDescending(request, out var sortDescending, out var sortError))
            {
                return ToJsonResult(Error(sortError));
            }

            var (messages, total) = await messageRepository.QueryAdminPageAsync(
                appKey,
                hwid,
                isRead,
                keyword,
                sortDescending,
                pageIndex,
                pageSize,
                cancellationToken);

            var sortLabel = sortDescending ? "desc" : "asc";
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = messages,
                otherData = $"total={total}; pageindex={pageIndex}; pagesize={pageSize}; count={messages.Count}; sort={sortLabel}"
            });
        }

        // DELETE /api/admin/messages：body 传 { "ids": ["..."] } 按 ID 删除。
        public async Task<IResult> HandleDeleteByIdsAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            DisableResponseCaching(request);

            if (!IsManagerAuthorized(request))
            {
                return ToJsonResult(Error("管理密码不正确。"), StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return ToJsonResult(Error("请求体不能为空，请传入 { \"ids\": [\"...\"] }。"));
            }

            DeleteByIdsRequest? body;
            try
            {
                body = JsonSerializer.Deserialize<DeleteByIdsRequest>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return ToJsonResult(Error("JSON 格式不正确，请传入 { \"ids\": [\"...\"] }。"));
            }

            var ids = body?.Ids?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? [];

            if (ids.Length == 0)
            {
                return ToJsonResult(Error("ids 不能为空。"));
            }

            var deleted = await messageRepository.DeleteByIdsAsync(ids, cancellationToken);
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "删除成功",
                otherData = $"deleted={deleted}; requested={ids.Length}"
            });
        }

        // DELETE /api/admin/messages/filter：按与列表相同的筛选条件批量删除。
        public async Task<IResult> HandleDeleteByFilterAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            DisableResponseCaching(request);

            if (!IsManagerAuthorized(request))
            {
                return ToJsonResult(Error("管理密码不正确。"), StatusCodes.Status403Forbidden);
            }

            var appKey = request.Query["appkey"].ToString().Trim();
            var hwid = request.Query["hwid"].ToString().Trim();
            var keyword = request.Query["keyword"].ToString().Trim();
            if (!TryParseOptionalIsRead(request, out var isRead, out var isReadError))
            {
                return ToJsonResult(Error(isReadError));
            }

            // 无任何筛选时要求显式 confirm=all，防止误删全库。
            var hasFilter = !string.IsNullOrWhiteSpace(appKey) ||
                            !string.IsNullOrWhiteSpace(hwid) ||
                            isRead.HasValue ||
                            !string.IsNullOrWhiteSpace(keyword);
            var confirmAll = string.Equals(
                request.Query["confirm"].ToString().Trim(),
                "all",
                StringComparison.OrdinalIgnoreCase);

            if (!hasFilter && !confirmAll)
            {
                return ToJsonResult(Error("未指定筛选条件时，必须传 confirm=all 才能清空全部消息。"));
            }

            var deleted = await messageRepository.DeleteByAdminFilterAsync(
                appKey,
                hwid,
                isRead,
                keyword,
                cancellationToken);

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "删除成功",
                otherData = $"deleted={deleted}"
            });
        }

        private bool IsManagerAuthorized(HttpRequest request)
        {
            var password = request.Headers[ManagerPasswordHeader].ToString();
            return appKeyManager.ValidateManagerPassword(password);
        }

        private static bool TryParsePage(
            HttpRequest request,
            out int pageIndex,
            out int pageSize,
            out string error)
        {
            pageIndex = 0;
            pageSize = 0;
            error = string.Empty;

            if (!int.TryParse(request.Query["pageindex"], out pageIndex) || pageIndex < 1)
            {
                error = "pageindex 必须是大于 0 的整数。";
                return false;
            }

            if (!int.TryParse(request.Query["pagesize"], out pageSize) || pageSize < 1)
            {
                error = "pagesize 必须是大于 0 的整数。";
                return false;
            }

            pageSize = Math.Min(pageSize, MaxPageSize);
            return true;
        }

        private static bool TryParseOptionalIsRead(
            HttpRequest request,
            out bool? isRead,
            out string error)
        {
            isRead = null;
            error = string.Empty;
            var value = request.Query["isread"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (value is "1" or "true" or "True")
            {
                isRead = true;
                return true;
            }

            if (value is "0" or "false" or "False")
            {
                isRead = false;
                return true;
            }

            error = "isread 只能是 0/1 或 true/false。";
            return false;
        }

        // sort=desc|asc（或 order=）；缺省按时间倒序。
        private static bool TryParseSortDescending(
            HttpRequest request,
            out bool sortDescending,
            out string error)
        {
            sortDescending = true;
            error = string.Empty;

            var value = request.Query["sort"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = request.Query["order"].ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (value.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("descending", StringComparison.OrdinalIgnoreCase))
            {
                sortDescending = true;
                return true;
            }

            if (value.Equals("asc", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("ascending", StringComparison.OrdinalIgnoreCase))
            {
                sortDescending = false;
                return true;
            }

            error = "sort 只能是 desc 或 asc。";
            return false;
        }

        private static void DisableResponseCaching(HttpRequest request)
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
        }

        private static IResult ToJsonResult(
            HxHttpResModel response,
            int statusCode = StatusCodes.Status200OK)
        {
            var json = JsonSerializer.Serialize(response, JsonOptions);
            return Results.Text(json, "application/json; charset=utf-8", Encoding.UTF8, statusCode);
        }

        private static HxHttpResModel Error(string message)
        {
            return new HxHttpResModel
            {
                code = 1,
                msg = message,
                otherData = string.Empty
            };
        }

        private sealed class DeleteByIdsRequest
        {
            public string[]? Ids { get; set; }
        }
    }
}
