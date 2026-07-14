# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息
- 这是一个控制台项目  负责处理消息推送的后台服务，使用 .NET 10.0。

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。

## 重要文件
- `Program.cs`：当前实现了一个简单 WebSocket 服务。默认监听 `http://localhost:5000/` 和本机可用的非回环 IPv4 地址，WebSocket 地址为 `/ws`；同局域网 APP 可连接启动输出里的 `ws://局域网IP:5000/ws`。启动参数可覆盖监听地址，例如 `dotnet run -- http://localhost:5123/ http://192.168.1.10:5123/`。
- `ws-test.html`：浏览器 WebSocket 测试页，可直接打开后填写 `ws://局域网IP:5000/ws` 进行连接、发送和接收日志测试。

## 已知问题
- 局域网设备访问时，Windows 防火墙或 `HttpListener` URL 保留权限可能会阻止连接；若启动时报权限错误，需要管理员运行或配置 URLACL/防火墙放行。已通过 `dotnet build` 编译验证；运行时 WebSocket 握手脚本因本地安全审查未执行。

## 进度记录
- 2026-07-13：实现了基础 WebSocket 服务。服务基于 `HttpListener`，支持 `/` 健康文本响应、`/ws` WebSocket 连接、文本消息接收、连接客户端广播、Ctrl+C 优雅停止；`dotnet build` 通过，0 警告 0 错误。
- 2026-07-13：将默认监听地址从仅 `localhost` 扩展为 `localhost` + 本机可用非回环 IPv4 地址；启动时会输出 HTTP/WebSocket 可连接地址，方便同局域网 APP 使用 `ws://局域网IP:5000/ws` 连接；`dotnet build` 通过，0 警告 0 错误。
- 2026-07-13：新增 `ws-test.html` 浏览器测试页，支持配置 WebSocket 地址、连接/断开、发送文本消息、查看收发日志，并提示局域网连接时注意防火墙放行端口。
