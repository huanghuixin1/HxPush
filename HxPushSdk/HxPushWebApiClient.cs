using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HxPushApp.models.Message;
using HxPushModel.HttpRequest;

namespace HxPushSdk;

/// <summary>
/// HxPushServerWeb HTTP API 客户端：封装消息与 AppKey 管理接口。
/// 注入的 <see cref="HttpClient"/> 由调用方持有；仅字符串构造创建的实例在 Dispose 时释放。
/// </summary>
public sealed class HxPushWebApiClient : IDisposable
{
    private const int MaxPageSize = 50;
    private const string ManagerPasswordHeader = "X-AppKey-Manager-Password";

    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions jsonOptions;
    /// <summary>为 true 时 Dispose 会释放内部创建的 HttpClient。</summary>
    private readonly bool ownsHttpClient;
    private bool disposed;

    /// <summary>
    /// 使用外部 HttpClient；不拥有其生命周期，适合 DI / 共享连接池场景。
    /// </summary>
    public HxPushWebApiClient(
        HttpClient httpClient,
        Uri baseAddress,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        jsonOptions = jsonSerializerOptions ?? CreateJsonSerializerOptions();
        BaseAddress = NormalizeBaseAddress(baseAddress ?? throw new ArgumentNullException(nameof(baseAddress)));
    }

    /// <summary>
    /// 按服务地址自建 HttpClient；ownsHttpClient=true，Dispose 时一并释放。
    /// </summary>
    public HxPushWebApiClient(string baseAddress)
    {
        httpClient = new HttpClient();
        ownsHttpClient = true;
        jsonOptions = CreateJsonSerializerOptions();
        BaseAddress = NormalizeBaseAddress(CreateHttpBaseAddress(baseAddress));
    }

    /// <summary>
    /// 规范化后的服务根地址（末尾带 /），例如 <c>http://127.0.0.1:5212/</c>。
    /// </summary>
    public Uri BaseAddress { get; }

    /// <summary>探活：GET 服务根路径。</summary>
    public Task<HxHttpResModel> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        return SendJsonAsync<HxHttpResModel>(HttpMethod.Get, "", null, null, cancellationToken);
    }

    /// <summary>
    /// 发送并持久化一条消息：POST /api/messages。
    /// </summary>
    public Task<HxHttpResModel> SendMessageAsync(
        HxPushMsgModel message,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return SendJsonAsync<HxHttpResModel>(
            HttpMethod.Post,
            "api/messages",
            message,
            null,
            cancellationToken);
    }

    /// <summary>
    /// 分页拉取消息：GET /api/messages。
    /// 提供 <paramref name="before"/> 时走游标分页（beforemsgdate + beforeid）；否则用 pageIndex/pageSize。
    /// </summary>
    public async Task<IReadOnlyList<HxPushMsgModel>> GetMessagesAsync(
        string appKey,
        int pageIndex = 1,
        int pageSize = MaxPageSize,
        string? hwid = null,
        HxPushMessageCursor? before = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAppKey(appKey);
        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than zero.");
        }

        pageSize = ValidatePageSize(pageSize);
        var query = new List<string>
        {
            $"appkey={Uri.EscapeDataString(appKey.Trim())}",
            $"pageindex={pageIndex}",
            $"pagesize={pageSize}"
        };

        AddOptionalQuery(query, "hwid", hwid);
        // 游标模式：用上一页末条的时间戳+ID 继续向前翻
        if (before is not null)
        {
            if (before.MsgDate < 1 || string.IsNullOrWhiteSpace(before.ID))
            {
                throw new ArgumentException("A cursor requires a positive MsgDate and a non-empty ID.", nameof(before));
            }

            query.Add($"beforemsgdate={before.MsgDate}");
            query.Add($"beforeid={Uri.EscapeDataString(before.ID)}");
        }

        var response = await SendJsonAsync<HxHttpResModel>(
            HttpMethod.Get,
            $"api/messages?{string.Join("&", query)}",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return DeserializeMessageList(response.msg);
    }

    /// <summary>
    /// 拉取未读消息：GET /api/messages/unread。服务端会将返回行标记为已读。
    /// </summary>
    public async Task<IReadOnlyList<HxPushMsgModel>> GetUnreadMessagesAsync(
        string appKey,
        string? hwid = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAppKey(appKey);
        var query = new List<string> { $"appkey={Uri.EscapeDataString(appKey.Trim())}" };
        AddOptionalQuery(query, "hwid", hwid);

        var response = await SendJsonAsync<HxHttpResModel>(
            HttpMethod.Get,
            $"api/messages/unread?{string.Join("&", query)}",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return DeserializeMessageList(response.msg);
    }

    /// <summary>
    /// 列出全部 AppKey：GET /api/appkeys（需管理密码请求头）。
    /// </summary>
    public async Task<IReadOnlyList<HxPushAppKeyModel>> GetAppKeysAsync(
        string managerPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateManagerPassword(managerPassword);
        var response = await SendJsonAsync<HxHttpResModel>(
            HttpMethod.Get,
            "api/appkeys",
            null,
            managerPassword,
            cancellationToken).ConfigureAwait(false);
        return DeserializeList<HxPushAppKeyModel>(response.msg);
    }

    /// <summary>
    /// 创建 AppKey：POST /api/appkeys。
    /// </summary>
    public async Task<HxPushAppKeyModel> CreateAppKeyAsync(
        string managerPassword,
        string? appKey = null,
        string? remark = null,
        CancellationToken cancellationToken = default)
    {
        ValidateManagerPassword(managerPassword);
        var body = new Dictionary<string, string?>
        {
            ["appkey"] = string.IsNullOrWhiteSpace(appKey) ? null : appKey.Trim(),
            ["remark"] = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim()
        };

        var response = await SendJsonAsync<HxHttpResModel>(
            HttpMethod.Post,
            "api/appkeys",
            body,
            managerPassword,
            cancellationToken).ConfigureAwait(false);
        var list = DeserializeList<HxPushAppKeyModel>(response.msg);
        return list.FirstOrDefault()
            ?? throw new JsonException("The server did not return a created AppKey.");
    }

    /// <summary>
    /// 更新 AppKey 备注：PUT /api/appkeys/{appKey}。
    /// </summary>
    public Task<HxHttpResModel> UpdateAppKeyRemarkAsync(
        string managerPassword,
        string appKey,
        string? remark,
        CancellationToken cancellationToken = default)
    {
        ValidateManagerPassword(managerPassword);
        ValidateAppKey(appKey);
        var body = new Dictionary<string, string?>
        {
            ["remark"] = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim()
        };

        return SendJsonAsync<HxHttpResModel>(
            HttpMethod.Put,
            $"api/appkeys/{Uri.EscapeDataString(appKey.Trim())}",
            body,
            managerPassword,
            cancellationToken);
    }

    /// <summary>
    /// 删除 AppKey：DELETE /api/appkeys/{appKey}。
    /// </summary>
    public Task<HxHttpResModel> DeleteAppKeyAsync(
        string managerPassword,
        string appKey,
        CancellationToken cancellationToken = default)
    {
        ValidateManagerPassword(managerPassword);
        ValidateAppKey(appKey);
        return SendJsonAsync<HxHttpResModel>(
            HttpMethod.Delete,
            $"api/appkeys/{Uri.EscapeDataString(appKey.Trim())}",
            null,
            managerPassword,
            cancellationToken);
    }

    /// <summary>
    /// 仅当 ownsHttpClient 为 true 时释放内部 HttpClient；注入客户端留给调用方处理。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    /// <summary>
    /// 统一 JSON 请求通道：拼 URL、可选管理密码头、序列化 body，并区分 HTTP 失败与业务 code!=0。
    /// </summary>
    private async Task<TResponse> SendJsonAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        object? body,
        string? managerPassword,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, relativePath));
        if (!string.IsNullOrWhiteSpace(managerPassword))
        {
            request.Headers.TryAddWithoutValidation(ManagerPasswordHeader, managerPassword);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        // 传输层失败：非 2xx
        if (!response.IsSuccessStatusCode)
        {
            throw new HxPushHttpException(response.StatusCode, content);
        }

        var result = JsonSerializer.Deserialize<TResponse>(content, jsonOptions)
            ?? throw new JsonException("The server returned an empty JSON response.");

        // 业务层失败：HTTP 成功但 envelope.code != 0
        if (result is HxHttpResModel apiResponse && apiResponse.code != 0)
        {
            throw new HxPushApiException(apiResponse.code, apiResponse.msg?.ToString() ?? string.Empty, apiResponse);
        }

        return result;
    }

    /// <summary>
    /// 将 envelope.msg 反序列化为列表；兼容 JsonElement 与已物化对象两种形态。
    /// </summary>
    private IReadOnlyList<T> DeserializeList<T>(object? value)
    {
        if (value is JsonElement element)
        {
            return element.Deserialize<List<T>>(jsonOptions) ?? new List<T>();
        }

        if (value is null)
        {
            return Array.Empty<T>();
        }

        // 非 JsonElement 时先再序列化一轮，避免类型不完全匹配
        return JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(value, jsonOptions), jsonOptions)
            ?? new List<T>();
    }

    private IReadOnlyList<HxPushMsgModel> DeserializeMessageList(object? value) =>
        DeserializeList<HxPushMsgModel>(value);

    /// <summary>
    /// 将 ws/wss 或带 /ws 路径的地址规范为 HTTP(S) 根地址，供 REST 调用使用。
    /// </summary>
    private Uri CreateHttpBaseAddress(string serverAddress)
    {
        if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out var uri))
        {
            throw new UriFormatException("The server address must be an absolute URI.");
        }

        // WebSocket scheme → 对应 HTTP scheme，便于同一 host/port 调 REST
        var scheme = uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttp
            : uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : uri.Scheme;
        var path = uri.AbsolutePath.TrimEnd('/');
        // 去掉常见 WebSocket 路径，避免 REST 请求落到 /ws
        if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
        }

        return new UriBuilder(scheme, uri.Host, uri.Port, path).Uri;
    }

    /// <summary>规范化 base address：HTTP 化并保证末尾斜杠，便于相对路径拼接。</summary>
    private Uri NormalizeBaseAddress(Uri baseAddress)
    {
        var normalized = CreateHttpBaseAddress(baseAddress.ToString());
        return new Uri(normalized.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }

    private void AddOptionalQuery(ICollection<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private int ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {MaxPageSize}.");
        }

        return pageSize;
    }

    private void ValidateAppKey(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw new ArgumentException("AppKey cannot be empty.", nameof(appKey));
        }
    }

    private void ValidateManagerPassword(string managerPassword)
    {
        if (string.IsNullOrWhiteSpace(managerPassword))
        {
            throw new ArgumentException("Manager password cannot be empty.", nameof(managerPassword));
        }
    }

    private JsonSerializerOptions CreateJsonSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(HxPushWebApiClient));
        }
    }
}

/// <summary>
/// 消息游标分页定位点：上一页末条的 MsgDate + ID。
/// </summary>
public sealed class HxPushMessageCursor
{
    public HxPushMessageCursor(long msgDate, string id)
    {
        MsgDate = msgDate;
        ID = id ?? throw new ArgumentNullException(nameof(id));
    }

    /// <summary>消息时间戳（与服务端 beforemsgdate 对应）。</summary>
    public long MsgDate { get; }

    /// <summary>消息 ID（与服务端 beforeid 对应）。</summary>
    public string ID { get; }
}

/// <summary>
/// HTTP 传输层异常：状态码非成功时抛出，保留响应正文便于排查。
/// </summary>
public sealed class HxPushHttpException : HttpRequestException
{
    public HxPushHttpException(HttpStatusCode statusCode, string responseBody)
        : base($"HxPush HTTP request failed with {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public new HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }
}

/// <summary>
/// 业务层异常：HTTP 成功但 HxHttpResModel.code != 0。
/// </summary>
public sealed class HxPushApiException : InvalidOperationException
{
    public HxPushApiException(int code, string message, HxHttpResModel response)
        : base(message)
    {
        Code = code;
        Response = response;
    }

    /// <summary>服务端业务错误码。</summary>
    public int Code { get; }

    /// <summary>完整响应 envelope，便于上层展示或日志。</summary>
    public HxHttpResModel Response { get; }
}

/// <summary>
/// AppKey 列表/创建接口的数据模型。
/// </summary>
public sealed class HxPushAppKeyModel
{
    public string AppKey { get; set; } = string.Empty;

    public string Remark { get; set; } = string.Empty;
}