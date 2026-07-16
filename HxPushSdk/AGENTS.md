# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息
- 目标框架：net8.0（依赖内置 System.Text.Json）。
- 主要类型：`HxPushWebApiClient`（HTTP API 客户端）及配套游标/异常/AppKey 模型。

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。
- 后续所有代码调整都必须同步添加或更新精简注释，重点说明职责、意图和关键分支，避免逐行复述代码。

## 重要文件
- `HxPushWebApiClient.cs`：对外 SDK 入口；封装消息收发、未读拉取、AppKey CRUD。
- `HxPushSdk.csproj`：引用 `HxPushModel`。
- `README.md`：SDK 调用说明与示例。

## 已知问题
- SDK 中 Create/Update/Delete AppKey 使用细粒度 REST；当前 ServerWeb 仅提供 GET 列表与 PUT 全量替换。

## 进度记录

# 2026-07-16: Added HxPushWebApiClient for HxPushServerWeb HTTP APIs. SDK targets net8.0 for built-in System.Text.Json support.
# 2026-07-16: Converted all HxPushSdk helper methods to instance methods. HxPushWebApiClient now implements IDisposable and only disposes HttpClient instances that it creates itself; injected clients remain caller-owned.
# 2026-07-16: 为 HxPushSdk 全量补充精简注释（职责/意图/关键分支）：构造器生命周期、游标分页、ws→http 规范化、HTTP 与业务双层错误、Dispose 所有权；并同步更新本文件项目信息与重要文件说明。
# 2026-07-16: 新增 HxPushSdk/README.md，详细说明 HxPushWebApiClient 接入方式、全部公开方法调用示例、异常约定、分页/游标、AppKey 管理，以及与当前 ServerWeb 路由差异。