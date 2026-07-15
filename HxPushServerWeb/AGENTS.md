# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。
- 每完成一个 Web 接口，都要同步在 `wwwroot/ws-test.html` 中增加对应的测试调用。
- 后续所有代码调整都必须同步添加或更新精简注释，重点说明职责、意图和关键分支，避免逐行复述代码。

## 重要文件
- `HxPushAppKeyManager.cs`：缓存、读取和覆盖保存 AppKey，并校验管理密码。
- `wwwroot/appkeyManager.html`：只负责受密码保护的 AppKey 读取与编辑。

## 已知问题

## 进度记录

- 2026-07-14: 为 HxPushServerWeb 添加 ASP.NET Core 原生 WebSocket 简单实现。`/ws` 接收 WebSocket 连接并广播文本消息，`/api/push` 支持 HTTP POST 推送消息到所有连接，`/ws-test.html` 可用于浏览器手动测试。
- 2026-07-14: 应学习需求将 WebSocket 实现精简为最小 Echo Server，并在 `Program.cs` 中加入分步骤中文注释。当前 `/ws` 只负责连接、接收文本、回发 `echo:` 文本，便于逐步理解。
- 2026-07-14: 将 HxPushServerWeb 默认监听地址改为 `http://0.0.0.0:5212`，并同步更新 `launchSettings.json`，便于局域网设备或服务器外部流量访问。
- 2026-07-14: 修复 WebSocket 收到 `exit` 后服务端 `CloseAsync` 仍继续 `SendAsync` 导致异常的问题；关闭连接后立即 `break` 跳出接收循环。
- 2026-07-14: 新增 `POST /api/messages` 接口，接收 `HxPushMsgModel` JSON 并写入 SQLite。数据库默认路径为 `App_Data/hxpush.db`，启动时自动创建 `HxPushMessages` 表。
- 2026-07-14: 将 HTTP 请求处理拆到 `HxPushHttpHandler`，WebSocket 处理拆到 `HxPushWebSocketHandler`，SQLite 写入拆到 `HxPushMessageRepository`。HTTP 接口统一返回 `HxHttpResModel` JSON 字符串，业务错误也保持 HTTP 200。
- 2026-07-14: 收尾调整 HTTP JSON 输出，保留中文原文不转义；WebSocket handler 改为直接处理请求，不再返回 `IResult?` 空值，并补充后台推送任务的关闭收尾。
- 2026-07-14: 扩充 `wwwroot/ws-test.html` 为接口测试台，包含 HTTP `/api/messages` JSON 提交、根路径和 `/ws` 普通 HTTP 返回测试，以及原 WebSocket 连接/发送/日志测试。
- 2026-07-14: 调整 `HxPushServerWeb.csproj`，让 `wwwroot` 静态文件构建时复制到输出目录，避免直接运行 `bin/Debug/net10.0/HxPushServerWeb.exe` 时测试页返回 404。
- 2026-07-14: 为 HxPushServerWeb 核心 C# 文件补充精炼注释，覆盖启动装配、HTTP 统一响应、WebSocket 收发/关闭、SQLite 建表和参数化写入等关键点。
- 2026-07-14: 将 SQLite 数据库路径从 `ContentRootPath/App_Data/hxpush.db` 调整为程序运行目录 `AppContext.BaseDirectory/App_Data/hxpush.db`，运行目录没有数据库时启动自动创建。
- 2026-07-14: 新增 `HxPushAppKeyManager`，从程序运行目录 `App_Data/appkeys.txt` 读取 AppKey 白名单；`POST /api/messages` 会校验 AppKey，白名单不存在时返回 HTTP 403 和 `HxHttpResModel` JSON。
- 2026-07-14: WebSocket 也改为接收 `HxPushMsgModel` JSON，校验 AppKey 失败会直接关闭连接；服务端保存 `/api/messages` 后会把消息 JSON 推送给同 AppKey 的已登记 WebSocket 客户端。
- 2026-07-14: 同步更新 `ws-test.html`，WebSocket 测试输入改为 `HxPushMsgModel` JSON，并增加发送不存在 AppKey 的测试按钮。
- 2026-07-15: 新增 `GET /api/messages?appkey=...&pageindex=...&pagesize=...` 分页查询接口，按 `MsgDate`、`ID` 倒序返回同 AppKey 的 `HxPushMsgModel` 数组；`HxHttpResModel.msg` 调整为可承载文本或数组，并为服务端消息表补充 `MsgDate` 字段及旧库迁移。
- 2026-07-15: 在 `wwwroot/ws-test.html` 增加消息列表 GET 测试区，可输入 `appkey`、`pageindex`、`pagesize` 并查看格式化响应；同时将“新增 Web 接口必须同步增加页面测试调用”加入长期规则。
- 2026-07-15: 为 HxPushServerWeb 启用全局宽松 CORS 策略，所有 HTTP 接口允许任意来源、请求头和请求方法；WebSocket 未设置 Origin 限制。
- 2026-07-15: 为 HxPushServerWeb 手写源码补齐精简注释，覆盖应用装配、HTTP、WebSocket、SQLite、AppKey 管理、项目配置和测试页面；长期要求后续代码调整必须同步维护注释。
- 2026-07-15: WebSocket `/ws` 改为在握手 URL 中接收 `appkey` 并于协议升级前校验，无效值返回 403；连接建立后 AppKey 固定，消息 AppKey 不一致会关闭连接，测试页面同步增加握手 AppKey 输入。
- 2026-07-15: 新增 `/appkeyManager.html` 和受管理密码保护的 `GET/PUT /api/appkeys`；密码存放于运行目录 `App_Data/appkey-password.txt`，缺失时默认为 `123`。`HxPushAppKeyManager.Exists` 改为查询启动时加载、管理保存时同步更新的内存缓存，不再逐请求读取白名单文件；`ws-test.html` 同步增加管理接口测试入口。
