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
        private readonly HxPushMessageRepository messageRepository;
        private readonly HxPushAppKeyManager appKeyManager;

        // 让响应里的中文保持可读，不转成 \uXXXX。
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public HxPushHttpHandler(
            HxPushMessageRepository messageRepository,
            HxPushAppKeyManager appKeyManager)
        {
            this.messageRepository = messageRepository;
            this.appKeyManager = appKeyManager;
        }

        public IResult HandleIndex()
        {
            // 根路径也按项目约定返回 HxHttpResModel JSON。
            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "HxPushServerWeb is running.",
                otherData = "Open /ws-test.html, connect WebSocket at /ws, or POST HxPushMsgModel JSON to /api/messages."
            });
        }

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

            // 客户端不传 ID 时由服务端生成，避免主键为空。
            message.ID = Guid.NewGuid().ToString("N");

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
                // SQLite 19 通常是约束冲突，这里主要对应 ID 主键重复。
                return ToJsonResult(Error($"ID 已存在，不能重复写入：{message.ID}"));
            }

            return ToJsonResult(new HxHttpResModel
            {
                code = 0,
                msg = "保存成功",
                otherData = message.ID
            });
        }

        public static IResult ToJsonResult(HxHttpResModel response, int statusCode = StatusCodes.Status200OK)
        {
            // 默认业务错误也返回 200；需要明确拒绝访问时可传入 403。
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
    }
}
