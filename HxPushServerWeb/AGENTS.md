# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。

## 重要文件

## 已知问题

## 进度记录

- 2026-07-14: 为 HxPushServerWeb 添加 ASP.NET Core 原生 WebSocket 简单实现。`/ws` 接收 WebSocket 连接并广播文本消息，`/api/push` 支持 HTTP POST 推送消息到所有连接，`/ws-test.html` 可用于浏览器手动测试。
- 2026-07-14: 应学习需求将 WebSocket 实现精简为最小 Echo Server，并在 `Program.cs` 中加入分步骤中文注释。当前 `/ws` 只负责连接、接收文本、回发 `echo:` 文本，便于逐步理解。
- 2026-07-14: 将 HxPushServerWeb 默认监听地址改为 `http://0.0.0.0:5212`，并同步更新 `launchSettings.json`，便于局域网设备或服务器外部流量访问。
- 2026-07-14: 修复 WebSocket 收到 `exit` 后服务端 `CloseAsync` 仍继续 `SendAsync` 导致异常的问题；关闭连接后立即 `break` 跳出接收循环。
