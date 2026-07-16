using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HxPushApp.models.Message;
using HxPushModel.HttpRequest;
using Microsoft.Data.Sqlite;

namespace HxPushServerWeb
{
    // 负责 HTTP 接口的参数校验、业务编排和统一 JSON 响应。
    internal sealed class HxPushHttpHandler
    {
        private const string AppKeyManagerPasswordHeader = "X-AppKey-Manager-Password";
        private const int MaxMessagePageSize = 50;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        private readonly HxPushMessageRepository messageRepository;
        private readonly HxPushAppKeyManager appKeyManager;
        private readonly HxPushWebSocketHandler webSocketHandler;

        public HxPushHttpHandler(
            HxPushMessageRepository messageRepository,
            HxPushAppKeyManager appKeyManager,
            HxPushWebSocketHandler webSocketHandler)
        {
            this.messageRepository = messageRepository;
            this.appKeyManager = appKeyManager;
            this.webSocketHandler = webSocketHandler;
        }

        public IResult HandleIndex()
        {
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "HxPushServerWeb is running.",
                otherData = "Open /ws-test.html, /webapi.html, /appkeyManager.html or /msgManager.html."
            });
        }

        // 保存 HTTP 推送消息，并按 AppKey 推给在线 WebSocket 客户端。
        public async Task<IResult> HandleCreateMessageAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                return ToJsonResult(Error("请求体不能为空，请传入 HxPushMsgModel JSON。"));
            }

            HxPushMsgModel? message;

            try
            {
                message = JsonSerializer.Deserialize<HxPushMsgModel>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return ToJsonResult(Error("JSON 格式不正确，无法转换为 HxPushMsgModel。"));
            }

            if (message is null)
            {
                return ToJsonResult(Error("JSON 内容不能为空。"));
            }

            message.ID = Guid.NewGuid().ToString("N");
            message.MsgDate = 0;
            message.IsRead = false;

            if (string.IsNullOrWhiteSpace(message.AppKey) ||
                string.IsNullOrWhiteSpace(message.Hwid) ||
                string.IsNullOrWhiteSpace(message.Msg))
            {
                return ToJsonResult(Error("AppKey、Hwid、Msg 都不能为空。"));
            }

            if (!appKeyManager.Exists(message.AppKey))
            {
                return ToJsonResult(
                    Error($"AppKey 不存在：{message.AppKey}"),
                    StatusCodes.Status403Forbidden);
            }

            try
            {
                await messageRepository.InsertAsync(message, cancellationToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return ToJsonResult(Error($"ID 已存在，不能重复写入：{message.ID}"));
            }

            var pushCount = await webSocketHandler.SendToAppKeyAsync(message, cancellationToken);

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "保存成功",
                // SendAsync 只代表数据已交给系统缓冲区；实际已读状态必须等待 App 写入 SQLite 后回 ACK。
                otherData = $"ID={message.ID}; pushed={pushCount}; delivery=ackRequired"
            });
        }

        // GET /api/messages 支持 pageindex 旧分页，也支持 beforemsgdate + beforeid 游标分页。
        public async Task<IResult> HandleGetMessagesAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            var appKey = request.Query["appkey"].ToString().Trim();
            var hwid = request.Query["hwid"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(appKey))
            {
                return ToJsonResult(Error("appkey 不能为空。"));
            }

            if (!int.TryParse(request.Query["pageindex"], out var pageIndex) || pageIndex < 1)
            {
                return ToJsonResult(Error("pageindex 必须是大于 0 的整数。"));
            }

            if (!int.TryParse(request.Query["pagesize"], out var pageSize) || pageSize < 1)
            {
                return ToJsonResult(Error("pagesize 必须是大于 0 的整数。"));
            }

            pageSize = Math.Min(pageSize, MaxMessagePageSize);
            var beforeCursorResult = ParseBeforeCursor(request);
            if (beforeCursorResult.ErrorMessage is not null)
            {
                return ToJsonResult(Error(beforeCursorResult.ErrorMessage));
            }

            if (!appKeyManager.Exists(appKey))
            {
                return ToJsonResult(
                    Error($"AppKey 不存在：{appKey}"),
                    StatusCodes.Status403Forbidden);
            }

            var messages = await messageRepository.GetPageAndMarkReadAsync(
                appKey,
                hwid,
                pageIndex,
                pageSize,
                beforeCursorResult.BeforeMsgDate,
                beforeCursorResult.BeforeId,
                cancellationToken);

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = messages,
                otherData = $"count={messages.Count}; pagesize={pageSize}"
            });
        }

        // 获取未读消息，并在返回后标记本次结果为已读。
        public async Task<IResult> HandleGetUnreadMessagesAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            var appKey = request.Query["appkey"].ToString().Trim();
            var hwid = request.Query["hwid"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(appKey))
            {
                return ToJsonResult(Error("appkey 不能为空。"));
            }

            if (!appKeyManager.Exists(appKey))
            {
                return ToJsonResult(
                    Error($"AppKey 不存在：{appKey}"),
                    StatusCodes.Status403Forbidden);
            }

            var messages = await messageRepository.GetUnreadAndMarkReadAsync(
                appKey,
                hwid,
                cancellationToken);

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = messages,
                otherData = $"count={messages.Count}"
            });
        }

        public IResult HandleGetAppKeys(HttpRequest request)
        {
            DisableResponseCaching(request);

            if (!IsAppKeyManagerAuthorized(request))
            {
                return ToJsonResult(Error("管理密码不正确。"), StatusCodes.Status403Forbidden);
            }

            var appKeys = appKeyManager.GetAll();
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = appKeys,
                otherData = $"count={appKeys.Count}"
            });
        }

        public async Task<IResult> HandleReplaceAppKeysAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            DisableResponseCaching(request);

            if (!IsAppKeyManagerAuthorized(request))
            {
                return ToJsonResult(Error("管理密码不正确。"), StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);
            HxPushAppKeyModel[]? appKeys;

            try
            {
                appKeys = DeserializeAppKeys(json);
            }
            catch (JsonException)
            {
                return ToJsonResult(Error("请求体必须是包含 AppKey 和 Remark 的对象数组。"));
            }

            if (appKeys is null)
            {
                return ToJsonResult(Error("请求体必须是包含 AppKey 和 Remark 的对象数组。"));
            }

            try
            {
                appKeyManager.ReplaceAll(appKeys);
            }
            catch (ArgumentException exception)
            {
                return ToJsonResult(Error(exception.Message));
            }

            var savedCount = appKeyManager.GetAll().Count;
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "保存成功",
                otherData = $"count={savedCount}"
            });
        }

        private static BeforeCursorResult ParseBeforeCursor(HttpRequest request)
        {
            var beforeMsgDateValue = request.Query["beforemsgdate"].ToString().Trim();
            var beforeId = request.Query["beforeid"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(beforeMsgDateValue) &&
                string.IsNullOrWhiteSpace(beforeId))
            {
                return new BeforeCursorResult(null, null, null);
            }

            if (!long.TryParse(beforeMsgDateValue, out var beforeMsgDate) || beforeMsgDate < 1)
            {
                return new BeforeCursorResult(null, null, "beforemsgdate 必须是大于 0 的整数。");
            }

            if (string.IsNullOrWhiteSpace(beforeId))
            {
                return new BeforeCursorResult(null, null, "使用 beforemsgdate 时必须同时传入 beforeid。");
            }

            return new BeforeCursorResult(beforeMsgDate, beforeId, null);
        }

        private static HxPushAppKeyModel[]? DeserializeAppKeys(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new List<HxPushAppKeyModel>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    values.Add(new HxPushAppKeyModel { AppKey = element.GetString() ?? string.Empty });
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var value = element.Deserialize<HxPushAppKeyModel>(JsonOptions);
                if (value is null)
                {
                    return null;
                }

                values.Add(value);
            }

            return values.ToArray();
        }

        private bool IsAppKeyManagerAuthorized(HttpRequest request)
        {
            var password = request.Headers[AppKeyManagerPasswordHeader].ToString();
            return appKeyManager.ValidateManagerPassword(password);
        }

        private static void DisableResponseCaching(HttpRequest request)
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
        }

        public static IResult ToJsonResult(
            HxHttpResModel response,
            int statusCode = StatusCodes.Status200OK)
        {
            var json = JsonSerializer.Serialize(response, JsonOptions);
            return Results.Text(json, "application/json; charset=utf-8", Encoding.UTF8, statusCode);
        }

        public static HxHttpResModel Error(string message)
        {
            return new HxHttpResModel
            {
                code = 1,
                msg = message,
                otherData = string.Empty
            };
        }

        private sealed record BeforeCursorResult(
            long? BeforeMsgDate,
            string? BeforeId,
            string? ErrorMessage);
    }
}
