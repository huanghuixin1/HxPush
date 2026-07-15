using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HxPushApp.models.Message;
using HxPushModel.HttpRequest;
using Microsoft.Data.Sqlite;

namespace HxPushServerWeb
{
    // 负责普通 HTTP 接口：读取请求、校验参数、调用仓储、统一返回 HxHttpResModel。
    internal sealed class HxPushHttpHandler
    {
        private const string AppKeyManagerPasswordHeader = "X-AppKey-Manager-Password";

        // HTTP 层只协调依赖，数据库和 WebSocket 细节由对应组件负责。
        private readonly HxPushMessageRepository messageRepository;
        private readonly HxPushAppKeyManager appKeyManager;
        private readonly HxPushWebSocketHandler webSocketHandler;

        // 让响应里的中文保持可读，不转成 \uXXXX。
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // 注入消息仓储、白名单和 WebSocket 推送处理器。
        public HxPushHttpHandler(
            HxPushMessageRepository messageRepository,
            HxPushAppKeyManager appKeyManager,
            HxPushWebSocketHandler webSocketHandler)
        {
            this.messageRepository = messageRepository;
            this.appKeyManager = appKeyManager;
            this.webSocketHandler = webSocketHandler;
        }

        // 返回服务运行状态和主要入口提示。
        public IResult HandleIndex()
        {
            // 根路径也按项目约定返回 HxHttpResModel JSON。
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "HxPushServerWeb is running.",
                otherData = "Open /ws-test.html, connect WebSocket at /ws?appkey=..., or use GET/POST /api/messages."
            });
        }

        // 校验、保存 POST 消息，并推送给同 AppKey 的在线客户端。
        public async Task<IResult> HandleCreateMessageAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            // 这里按需求接收“原始 JSON 字符串”，再手动反序列化为 HxPushMsgModel。
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                return ToJsonResult(Error("请求体不能为空，请传入 HxPushMsgModel 的 JSON 字符串。"));
            }

            HxPushMsgModel? message;

            try
            {
                // 忽略属性名大小写，方便前端传 id/appKey 或 ID/AppKey。
                message = JsonSerializer.Deserialize<HxPushMsgModel>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            catch (JsonException)
            {
                return ToJsonResult(Error("JSON 格式不正确，无法转换为 HxPushMsgModel。"));
            }

            if (message is null)
            {
                return ToJsonResult(Error("JSON 内容不能为空。"));
            }

            // ID 由服务端统一生成，避免信任客户端主键。
            message.ID = Guid.NewGuid().ToString("N");

            if (message.MsgDate <= 0)
            {
                // 未提供发送时间时使用当前 UTC 秒级时间戳。
                message.MsgDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            // 入库和推送依赖这些核心字段。
            if (string.IsNullOrWhiteSpace(message.AppKey) ||
                string.IsNullOrWhiteSpace(message.Hwid) ||
                string.IsNullOrWhiteSpace(message.Msg))
            {
                return ToJsonResult(Error("AppKey、Hwid、Msg 都不能为空。"));
            }

            // 白名单校验失败属于明确的访问拒绝，返回 HTTP 403。
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
                // SQLite 19 通常是约束冲突，这里主要对应 ID 主键重复。
                return ToJsonResult(Error($"ID 已存在，不能重复写入：{message.ID}"));
            }

            // 保存成功后再推送，保证客户端收到的消息已经持久化。
            var pushCount = await webSocketHandler.SendToAppKeyAsync(message, cancellationToken);

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "保存成功",
                otherData = $"ID={message.ID}; pushed={pushCount}"
            });
        }

        // 处理 GET 消息列表的参数校验和分页查询。
        public async Task<IResult> HandleGetMessagesAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            // 查询参数使用小写名称，与公开接口约定保持一致。
            var appKey = request.Query["appkey"].ToString().Trim();

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

            // 只允许查询白名单内 AppKey 的消息。
            if (!appKeyManager.Exists(appKey))
            {
                return ToJsonResult(
                    Error($"AppKey 不存在：{appKey}"),
                    StatusCodes.Status403Forbidden);
            }

            var messages = await messageRepository.GetPageAsync(
                appKey,
                pageIndex,
                pageSize,
                cancellationToken);

            // 列表直接放入 msg，序列化后是 JSON 数组而不是字符串。
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = messages,
                otherData = string.Empty
            });
        }

        // 读取全部 AppKey；管理密码不正确时不返回任何列表内容。
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

        // 使用 JSON 字符串数组覆盖 AppKey，并同步更新文件和内存缓存。
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
            string[]? appKeys;

            try
            {
                appKeys = JsonSerializer.Deserialize<string[]>(json);
            }
            catch (JsonException)
            {
                return ToJsonResult(Error("请求体必须是 AppKey 字符串数组。"));
            }

            if (appKeys is null)
            {
                return ToJsonResult(Error("请求体必须是 AppKey 字符串数组。"));
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

        // 管理接口统一从自定义请求头读取密码，避免密码出现在 URL 和访问日志中。
        private bool IsAppKeyManagerAuthorized(HttpRequest request)
        {
            var password = request.Headers[AppKeyManagerPasswordHeader].ToString();
            return appKeyManager.ValidateManagerPassword(password);
        }

        // AppKey 列表和管理结果不允许被浏览器或中间代理缓存。
        private static void DisableResponseCaching(HttpRequest request)
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
        }

        // 将统一响应模型输出为保留中文的 JSON。
        public static IResult ToJsonResult(HxHttpResModel response, int statusCode = StatusCodes.Status200OK)
        {
            // 默认业务错误也返回 200；需要明确拒绝访问时可传入 403。
            var json = JsonSerializer.Serialize(response, JsonOptions);
            return Results.Text(json, "application/json; charset=utf-8", Encoding.UTF8, statusCode);
        }

        // 创建 code=1 的统一业务错误响应。
        public static HxHttpResModel Error(string message)
        {
            return new HxHttpResModel
            {
                code = 1,
                msg = message,
                otherData = string.Empty
            };
        }
    }
}
