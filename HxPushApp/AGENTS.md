# Project Memory

以后处理这个项目时，先读这个文件。

## 项目信息
- 这是一个 .NET MAUI 单项目应用，主要页面通过 Shell/TabBar 组织。

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。

## 重要文件
- `AppShell.xaml` / `AppShell.xaml.cs`：底部 TabBar 和 Shell 路由注册。
- `MainPage.xaml` / `MainPage.xaml.cs`：首页，“查看消息”按钮会跳到消息 tab。
- `MessagesPage.xaml` / `MessagesPage.xaml.cs`：消息列表，从 SQLite 加载最近消息，支持下拉刷新与滚动分页。
- `MessageDetailPage.xaml` / `MessageDetailPage.xaml.cs`：消息详情页，显示完整消息内容。
- `Helpers/Sqlite/SqliteHelper.cs`：本地消息存储与按 `MsgDate + ID` 游标分页查询。
- `Helpers/AppSettings.cs`：基于 MAUI `Preferences` 集中管理 AppKey 等非敏感本地键值配置。

## 已知问题

## 进度记录
- 2026-07-13：修复发布问题；Android manifest 不再手写非法 `versionCode="1.0"`，恢复 MAUI 平台文件自动参与编译。
- 2026-07-13：把应用改成 Shell TabBar，包含首页、消息、设置三个 tab；首页“查看消息”按钮跳转到消息 tab。
- 2026-07-13：消息 tab 改为列表页，从 `mock/msgList.json` 打包资源读取假数据并展示。
- 2026-07-13：新增消息详情页；点击消息列表 item 会打开详情页显示完整内容。消息列表 item 增加高度限制，内容最多显示 2 行，超出用省略号截断。
- 2026-07-13：将解决方案重命名为 `HxPush`，并把项目文件夹/项目名改为 `HxPushApp`；同步更新命名空间、XAML `x:Class`、Windows manifest 和项目标识。 
- 2026-07-13：将 `HxPushApp` 配置为 Android-only；移除 iOS、MacCatalyst、Windows 目标框架和 Windows 设计器用户配置残留，平台文件夹暂保留但不参与目标框架编译。
- 2026-07-14：设置页新增 WebSocket 通信测试按钮，连接 `ws://192.168.31.119:5212/ws`，发送测试文本并显示服务端响应。
- 2026-07-14：为界面设计预览恢复 Windows 目标框架 `net10.0-windows10.0.19041.0`，可用 Visual Studio 的 XAML Live Preview / Hot Reload 减少 Android 反复部署；新增 `DESIGN_PREVIEW.md` 记录使用方式。
- 2026-07-14：完善设置页 WebSocket 逻辑；连接后启动独立接收任务持续读取服务端消息，支持分片文本/二进制提示、页面离开时取消并关闭连接，发送按钮会复用现有连接。
- 2026-07-14：修复设置页 WebSocket 日志区域无法滚动；外层布局从 `VerticalStackLayout` 改为行高受控的 `Grid`，日志 `ScrollView` 占剩余空间并在追加消息后自动滚到底。
- 2026-07-14：取消设置页 WebSocket 日志追加后的自动滚到底行为，避免新消息刷新时打断用户手动查看历史日志。
- 2026-07-14：将设置页 WebSocket 连接、发送、持续接收和关闭逻辑抽取到 `Helpers/WebSocketClientHelper.cs`；`SettingsPage.xaml.cs` 仅保留 UI 事件与日志更新，同时在项目文件中为 `SettingsPage.xaml.cs` 添加 `DependentUpon` 修复 VS 文件嵌套展示。
- 2026-07-14：为 `Helpers/WebSocketClientHelper.cs` 和 `SettingsPage.xaml.cs` 补充注释，说明 WebSocket 连接、发送、后台接收、断开以及设置页 UI 事件职责。
- 2026-07-14：新增 `Helpers/Sqlite/SqliteHelper.cs`，使用 `sqlite-net-pcl` 保存 `HxPushModel` 中的 `HxPushMsgModel`；设置页收到 WebSocket 文本后仅负责解析和调用存储，WebSocket helper 与 SQLite helper 保持解耦；删除 App 内空的同名消息模型以避免遮蔽共享模型。
- 2026-07-15：升级 NuGet 包：`Microsoft.Maui.Controls` 10.0.20 → 10.0.80、`Microsoft.Extensions.Logging.Debug` 10.0.0 → 10.0.10、`sqlite-net-pcl` 1.9.172 → 1.11.285；新版 sqlite-net 已改用 SQLitePCLRaw 3.0.3 与 SourceGear SQLite 3.53.3，因此移除已弃用的 `SQLitePCLRaw.bundle_green` 2.1.11 及旧式手动初始化，并消除其 `NU1903` 漏洞提示。
- 2026-07-15：消息列表改为读取 SQLite 中的 `HxPushMsgModel`；支持下拉清空并刷新最近 50 条、滚动到底按 `MsgDate + ID` 游标继续加载，以及无更多记录时显示短暂 Toast；详情页同步使用 SQLite 消息模型，并将绑定属性改名为 `MessageContent` 消除成员隐藏警告。
- 2026-07-15：设置页新增 AppKey 输入框与保存按钮，首次默认显示 `2222`；使用集中式 `AppSettings` 封装 MAUI `Preferences` 本地键值存储，空值不覆盖已保存配置，并在页面显示保存结果。
- 2026-07-15：设置页的 WebSocket 测试连接和手动发送统一改为序列化完整 `HxPushMsgModel` JSON；载荷携带已保存 AppKey、每条消息的 GUID、秒级时间戳，以及首次生成并持久化复用的安装级 Hwid。
- 2026-07-15：设置页新增与测试连接按钮同行的“断开连接”按钮；WebSocket helper 新增连接状态事件，主动断开、服务端关闭或接收异常时统一恢复“测试连接可用、断开连接置灰”，连接成功时状态反转。
- 2026-07-15：WebSocket helper 的连接方法改为接收 AppKey 并以 URL 查询参数提交握手校验；AppKey 变化时自动断开旧连接，设置页“测试连接”成功后直接保持接收，不再依赖自动发送测试消息完成登记。
