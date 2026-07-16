# HxPushSdk 使用说明

`HxPushSdk` 是面向 **HxPushServerWeb** 的 .NET HTTP 客户端库，封装消息推送/查询与 AppKey 管理相关接口。

- 目标框架：`net8.0`
- 入口类型：`HxPushSdk.HxPushWebApiClient`
- 依赖项目：`HxPushModel`（消息模型、统一响应 envelope）

---

## 1. 接入方式

### 1.1 引用项目

在你的应用 `.csproj` 中添加项目引用：

```xml
<ItemGroup>
  <ProjectReference Include="..\HxPushSdk\HxPushSdk.csproj" />
</ItemGroup>
```

或在 Visual Studio / Rider 中添加对 `HxPushSdk` 的 Project Reference。

### 1.2 命名空间

```csharp
using HxPushSdk;
using HxPushApp.models.Message;   // HxPushMsgModel
using HxPushModel.HttpRequest;    // HxHttpResModel
```

### 1.3 创建客户端

支持两种构造方式，**生命周期语义不同**，请按场景选择。

#### 方式 A：字符串地址（SDK 自建并持有 HttpClient）

适合控制台、脚本、一次性调用。`Dispose` 时会释放内部 `HttpClient`。

```csharp
using var client = new HxPushWebApiClient("http://127.0.0.1:5212");

// 也支持传入 WebSocket 地址，SDK 会自动规范为 HTTP 根地址：
// ws://host:5212/ws  -> http://host:5212/
// wss://host:5212    -> https://host:5212/
```

#### 方式 B：注入外部 HttpClient（推荐用于 DI / 长生命周期应用）

适合 ASP.NET Core、MAUI、后台服务等共享连接池场景。**SDK 不会 Dispose 注入的 HttpClient**。

```csharp
// 例如在 DI 中：
// builder.Services.AddHttpClient();
// builder.Services.AddSingleton(sp =>
// {
//     var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
//     return new HxPushWebApiClient(http, new Uri("http://127.0.0.1:5212"));
// });

var http = new HttpClient(); // 或 IHttpClientFactory 创建
var client = new HxPushWebApiClient(http, new Uri("http://127.0.0.1:5212"));
// 可选：自定义 JsonSerializerOptions
// var client = new HxPushWebApiClient(http, new Uri("http://127.0.0.1:5212"), myJsonOptions);
```

#### BaseAddress 说明

- 构造后可通过 `client.BaseAddress` 查看规范化结果。
- 规范化规则：
  - `ws` → `http`，`wss` → `https`
  - 去掉末尾路径 `/ws`
  - 保证末尾带 `/`，便于拼接相对路径

---

## 2. 统一约定

### 2.1 服务端响应 envelope

服务端统一返回 `HxHttpResModel`：

| 字段 | 含义 |
|------|------|
| `code` | `0` 表示业务成功；非 `0` 表示业务失败 |
| `msg` | 提示文字，或列表接口的业务数据 |
| `otherData` | 附加信息字符串（如 `count=3`） |

SDK 在内部会：

1. HTTP 非 2xx → 抛出 `HxPushHttpException`
2. HTTP 成功但 `code != 0` → 抛出 `HxPushApiException`
3. 列表类接口再把 `msg` 反序列化为具体模型列表后返回

因此多数“查询列表”方法的返回值已经是强类型列表，**不需要**你再手动解析 envelope。

### 2.2 异常类型

| 类型 | 触发条件 | 常用字段 |
|------|----------|----------|
| `HxPushHttpException` | HTTP 状态码失败 | `StatusCode`、`ResponseBody` |
| `HxPushApiException` | 业务 `code != 0` | `Code`、`Response`（完整 envelope） |
| `ArgumentException` / `ArgumentOutOfRangeException` | 参数校验失败 | 方法参数相关 |
| `ObjectDisposedException` | 客户端已 Dispose 后继续调用 | - |
| `JsonException` | 响应无法反序列化，或创建 AppKey 未返回数据 | - |

示例：

```csharp
try
{
    var messages = await client.GetMessagesAsync("your-app-key");
}
catch (HxPushHttpException ex)
{
    // 网络/HTTP 层：404、500 等
    Console.WriteLine($"HTTP {(int)ex.StatusCode}: {ex.ResponseBody}");
}
catch (HxPushApiException ex)
{
    // 业务层：AppKey 不存在、参数不合法等
    Console.WriteLine($"API code={ex.Code}, msg={ex.Message}");
}
```

### 2.3 AppKey 与管理密码

- **业务接口**（发消息、查消息）使用消息/查询参数中的 `AppKey`，服务端会校验该 AppKey 是否已登记。
- **管理接口**（AppKey 列表等）需要管理密码，SDK 自动放入请求头：

```http
X-AppKey-Manager-Password: <managerPassword>
```

服务端默认管理密码通常写在运行目录 `App_Data/appkey-password.txt`（默认值可能是 `123`，以实际部署为准）。

### 2.4 分页限制

- `pageSize` 有效范围：`1` ~ `50`（超出由 SDK 直接抛参数异常）
- `pageIndex` 必须 `>= 1`

---

## 3. 接口一览

| SDK 方法 | HTTP | 说明 |
|----------|------|------|
| `GetIndexAsync` | `GET /` | 探活 |
| `SendMessageAsync` | `POST /api/messages` | 发送并持久化一条消息，在线客户端可被 WebSocket 推送 |
| `GetMessagesAsync` | `GET /api/messages` | 分页/游标拉取历史消息 |
| `GetUnreadMessagesAsync` | `GET /api/messages/unread` | 拉取未读；**服务端会将返回行标记为已读** |
| `GetAppKeysAsync` | `GET /api/appkeys` | 列出全部 AppKey（需管理密码） |
| `CreateAppKeyAsync` | `POST /api/appkeys` | 创建 AppKey（需管理密码） |
| `UpdateAppKeyRemarkAsync` | `PUT /api/appkeys/{appKey}` | 更新备注（需管理密码） |
| `DeleteAppKeyAsync` | `DELETE /api/appkeys/{appKey}` | 删除 AppKey（需管理密码） |

> **与当前 HxPushServerWeb 的对应关系请注意：**  
> 服务端目前稳定提供：`GET/POST /api/messages`、`GET /api/messages/unread`、`GET /api/appkeys`、以及 `PUT /api/appkeys`（**整表替换**）。  
> SDK 中的 `CreateAppKeyAsync` / `UpdateAppKeyRemarkAsync` / `DeleteAppKeyAsync` 对应更细粒度 REST 路径；若服务端尚未实现这些路由，调用会得到 HTTP 404 或类似错误。AppKey 全量替换目前可用服务端 `PUT /api/appkeys`（见下文补充说明）。

---

## 4. 调用示例

以下示例默认服务地址为 `http://127.0.0.1:5212`。

### 4.1 探活

```csharp
using var client = new HxPushWebApiClient("http://127.0.0.1:5212");

HxHttpResModel index = await client.GetIndexAsync();
// index.code == 0 表示成功
Console.WriteLine(index.msg);
```

### 4.2 发送消息

`HxPushMsgModel` 主要字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `ID` | `string` | 消息 ID；新建时通常可留空，由服务端生成 |
| `AppKey` | `string` | **必填**，区分业务/用户分组 |
| `Hwid` | `string` | **必填**，设备 ID |
| `Msg` | `string` | **必填**，消息正文 |
| `MsgDate` | `long` | Unix **毫秒**时间戳；服务端也可能重写 |
| `IsRead` | `bool` | 是否已读/已推送成功 |

```csharp
using var client = new HxPushWebApiClient("http://127.0.0.1:5212");

var message = new HxPushMsgModel
{
    AppKey = "demo-app",
    Hwid = "device-001",
    Msg = "hello from SDK",
    MsgDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
};

HxHttpResModel result = await client.SendMessageAsync(message);
Console.WriteLine($"code={result.code}, msg={result.msg}, other={result.otherData}");
```

要点：

- `AppKey` 必须已在服务端登记，否则业务失败（`HxPushApiException`）。
- 发送成功后，服务端会落库，并向该 AppKey 下在线 WebSocket 连接推送。

### 4.3 分页拉取历史消息

#### 页码分页

```csharp
IReadOnlyList<HxPushMsgModel> page1 = await client.GetMessagesAsync(
    appKey: "demo-app",
    pageIndex: 1,
    pageSize: 20,
    hwid: null); // 可选：只查某设备

foreach (var m in page1)
{
    Console.WriteLine($"{m.MsgDate} [{m.Hwid}] {m.Msg} read={m.IsRead}");
}
```

#### 游标分页（适合无限下拉）

传入上一页最后一条的 `MsgDate + ID`：

```csharp
var firstPage = await client.GetMessagesAsync("demo-app", pageIndex: 1, pageSize: 20);
if (firstPage.Count == 0)
{
    return;
}

var last = firstPage[^1];
var cursor = new HxPushMessageCursor(last.MsgDate, last.ID);

// before 有值时走 beforemsgdate + beforeid 游标参数
var nextPage = await client.GetMessagesAsync(
    appKey: "demo-app",
    pageIndex: 1,          // 游标模式下仍会传 pageindex，但以 before 为准继续向前翻
    pageSize: 20,
    before: cursor);
```

`HxPushMessageCursor`：

```csharp
var cursor = new HxPushMessageCursor(msgDate: 1710000000000L, id: "msg-id");
// cursor.MsgDate / cursor.ID
```

### 4.4 拉取未读消息

```csharp
// 注意：服务端会把本次返回的消息标记为已读
IReadOnlyList<HxPushMsgModel> unread = await client.GetUnreadMessagesAsync(
    appKey: "demo-app",
    hwid: "device-001"); // hwid 可选

Console.WriteLine($"unread count = {unread.Count}");
```

适用场景：客户端上线后补拉离线未读，避免重复展示。

### 4.5 AppKey 管理

管理类接口都需要 `managerPassword`。

#### 列出全部 AppKey

```csharp
IReadOnlyList<HxPushAppKeyModel> keys = await client.GetAppKeysAsync("123");
foreach (var key in keys)
{
    Console.WriteLine($"{key.AppKey} - {key.Remark}");
}
```

#### 创建 AppKey（SDK 方法）

```csharp
// appKey 为空时，期望服务端自动生成；remark 可选
HxPushAppKeyModel created = await client.CreateAppKeyAsync(
    managerPassword: "123",
    appKey: "demo-app",
    remark: "演示用");
Console.WriteLine(created.AppKey);
```

#### 更新备注 / 删除（SDK 方法）

```csharp
await client.UpdateAppKeyRemarkAsync("123", "demo-app", "新备注");
await client.DeleteAppKeyAsync("123", "demo-app");
```

#### 当前服务端“整表替换”补充说明

若你对接的是当前仓库里的 `HxPushServerWeb`，AppKey 写入接口是：

```http
PUT /api/appkeys
Header: X-AppKey-Manager-Password: <password>
Body: [ { "appKey":"a", "remark":"..." }, { "appKey":"b", "remark":"..." } ]
```

这是**全量覆盖**语义，不是单条增删改。在服务端补齐细粒度 REST 前，可用 `HttpClient` 直接调用该 PUT，或扩展 SDK。读取列表则继续用 `GetAppKeysAsync`。

---

## 5. 完整最小示例

```csharp
using System;
using System.Threading.Tasks;
using HxPushSdk;
using HxPushApp.models.Message;

class Program
{
    static async Task Main()
    {
        const string server = "http://127.0.0.1:5212";
        const string appKey = "demo-app";
        const string hwid = "device-001";

        using var client = new HxPushWebApiClient(server);

        // 1) 探活
        var index = await client.GetIndexAsync();
        Console.WriteLine($"index: code={index.code}");

        // 2) 发消息
        await client.SendMessageAsync(new HxPushMsgModel
        {
            AppKey = appKey,
            Hwid = hwid,
            Msg = $"hi {DateTime.Now:HH:mm:ss}",
            MsgDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        // 3) 查最近消息
        var list = await client.GetMessagesAsync(appKey, pageIndex: 1, pageSize: 10, hwid: hwid);
        Console.WriteLine($"messages: {list.Count}");

        // 4) 拉未读（会标记已读）
        var unread = await client.GetUnreadMessagesAsync(appKey, hwid);
        Console.WriteLine($"unread: {unread.Count}");
    }
}
```

---

## 6. 在 ASP.NET Core / MAUI 中注册（推荐）

```csharp
// Program.cs / MauiProgram.cs
builder.Services.AddHttpClient("hxpush");

builder.Services.AddSingleton<HxPushWebApiClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("hxpush");
    return new HxPushWebApiClient(http, new Uri("http://127.0.0.1:5212"));
});
```

使用：

```csharp
public class MyService
{
    private readonly HxPushWebApiClient _client;
    public MyService(HxPushWebApiClient client) => _client = client;

    public Task PushAsync(string text) =>
        _client.SendMessageAsync(new HxPushMsgModel
        {
            AppKey = "demo-app",
            Hwid = "server",
            Msg = text,
            MsgDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
}
```

注意：注入模式下**不要**对 `HxPushWebApiClient` 再包一层会释放注入 `HttpClient` 的错误 Dispose 逻辑；SDK 本身在注入构造时不会释放外部 `HttpClient`。

---

## 7. 相关类型速查

| 类型 | 命名空间 | 作用 |
|------|----------|------|
| `HxPushWebApiClient` | `HxPushSdk` | HTTP API 客户端 |
| `HxPushMessageCursor` | `HxPushSdk` | 游标分页定位点 |
| `HxPushAppKeyModel` | `HxPushSdk` | AppKey + 备注 |
| `HxPushHttpException` | `HxPushSdk` | HTTP 层异常 |
| `HxPushApiException` | `HxPushSdk` | 业务层异常 |
| `HxPushMsgModel` | `HxPushApp.models.Message` | 消息实体 |
| `HxHttpResModel` | `HxPushModel.HttpRequest` | 统一响应 |

---

## 8. 常见问题

**Q: 传入 `ws://.../ws` 会不会请求错地址？**  
A: 不会。SDK 会转为对应的 `http(s)://host:port/` 再调 REST。

**Q: 为什么发消息提示 AppKey 不存在？**  
A: 需要先在服务端登记 AppKey（管理接口或 `App_Data/appkeys.txt`），再发消息。

**Q: `GetUnreadMessagesAsync` 第二次为什么是空的？**  
A: 正常。该接口会把返回记录标记为已读。

**Q: `pageSize` 能传 100 吗？**  
A: 不能，SDK 限制最大 50。

**Q: 如何取消请求？**  
A: 各异步方法都支持 `CancellationToken`：

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await client.GetIndexAsync(cts.Token);
```

---

## 9. 本地联调建议

1. 启动 `HxPushServerWeb`（默认监听 `http://0.0.0.0:5212`）。
2. 确认 `App_Data/appkeys.txt` 中有可用 AppKey。
3. 用上面的最小示例发送/查询消息。
4. 需要实时推送时，客户端再连 WebSocket：`ws://host:5212/ws`（本 SDK 仅覆盖 HTTP API，不含 WebSocket 客户端）。