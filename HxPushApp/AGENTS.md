# Project Memory

## Latest Update
- 2026-07-17: 真机启动闪退修复：根因是 MAUI 默认 `Theme.MaterialComponents.DayNight` + 系统深色/强制深色时，Shell `BottomNavigationView` 的 `itemTextAppearance` 为空导致 NPE。修复：1) 新增 `Platforms/Android/Resources/values/styles.xml`，用 `Theme.MaterialComponents.Light.DarkActionBar` 覆盖 `Maui.MainTheme*`，并给底部导航显式指定 `TextAppearance.AppCompat.Caption`；2) `colors.xml` 补 `colorActionMenuTextColor`；3) `MainApplication` 静态构造 + `OnCreate`（在 `base` 前）设 `ModeNightNo`；4) `MainActivity.OnCreate` 再设一次；5) Android 禁止在 `App` 构造设 `UserAppTheme`（仅 Windows）；6) manifest `forceDarkAllowed=false`。
- 2026-07-17: The App uses a consistent light-blue theme. Android sets `AppCompatDelegate.ModeNightNo` early (static ctor / before Activity), disables forced dark mode, and uses native blue colors (`#2563EB`, `#1D4ED8`, `#38BDF8`); Windows sets MAUI `UserAppTheme` to Light. Do not set Android `UserAppTheme` in the MAUI `App` constructor: on some devices it leaves Shell's Material bottom navigation without a valid TextAppearance and crashes at startup.
- 2026-07-17: A successful manual connection navigates with a one-time Shell parameter; the Messages page consumes it and shows its existing bottom Toast with `连接成功` for 1500 ms after navigation.
- 2026-07-17: Settings connection actions now use dedicated visual states: enabled Connect is high-contrast blue, enabled Disconnect is high-contrast red, and disabled actions use muted gray backgrounds, text, and borders in both light and dark themes.
- 2026-07-16: WebSocket pushes now send a `deliveryAck` only after SQLite persistence. Normal window and Android activity teardown attempt a close handshake, but forced Android process termination is intentionally not used to decide read status.

以后处理这个项目时，先读这个文件。

## 项目信息
- 这是一个 .NET MAUI 单项目应用，主要页面通过 Shell/TabBar 组织。

## 长期规则
- 修改代码前，先理解当前项目结构。
- 重要结论不要只放在聊天里，要写回本文件。
- 每次完成任务后，在下面的「进度记录」追加一段总结。

## 重要文件
- `AppShell.xaml` / `AppShell.xaml.cs`：底部 TabBar（消息、设置）和 Shell 路由注册。
- `App.xaml.cs`：启动流程——初始化 SQLite，每次打开自动连接一次；无 AppKey 或连接失败进设置，连接成功进消息。Android 勿在构造期设 `UserAppTheme`。
- `Platforms/Android/Resources/values/styles.xml`：覆盖 `Maui.MainTheme*` 为固定浅色，并配置 `HxPushBottomNavigationView` 的 item TextAppearance，防止真机启动 NPE。
- `Platforms/Android/MainApplication.cs` / `MainActivity.cs`：尽早 `AppCompatDelegate.ModeNightNo`。
- `MessagesPage.xaml` / `MessagesPage.xaml.cs`：消息列表，从 SQLite 加载最近消息，支持下拉刷新与滚动分页。
- `MessageDetailPage.xaml` / `MessageDetailPage.xaml.cs`：消息详情页，显示完整消息内容。
- `Helpers/Sqlite/SqliteHelper.cs`：本地消息存储与按 `MsgDate + ID` 游标分页查询。
- `Helpers/AppSettings.cs`：基于 MAUI `Preferences` 集中管理 AppKey 等非敏感本地键值配置。
- `Helpers/PushConnectionService.cs`：应用级 WebSocket；连接成功后 maintainConnection=true，意外断开退避重连，前台恢复时 EnsureConnectedAsync 补连；主动断开关闭自动重连。
- `Helpers/HxPushMessageApiClient.cs`：消息拉取薄封装，只负责 AppSettings（AppKey/服务器地址）、WebSocket 已连接校验与 10 秒超时，HTTP 实现委托 `HxPushSdk.HxPushWebApiClient`；未连接时提示“未连接到服务器”。
- `HxPushSdk` 项目引用：所有面向 HxPushServerWeb 的 REST 请求统一走 SDK，避免 App 内重复 HTTP 代码。
- `Converters/UnixTimestampToTimeConverter.cs`：将服务端 UTC Unix 毫秒时间戳转换为设备本地时间；今天显示“今天 HH:mm:ss”，1 到 30 天内显示“N天前 HH:mm:ss”，更早显示 `yyyy-MM-dd HH:mm:ss`。

## 已知问题

## 进度记录
- 2026-07-13：修复发布问题；Android manifest 不再手写非法 `versionCode="1.0"`，恢复 MAUI 平台文件自动参与编译。
- 2026-07-13：把应用改成 Shell TabBar，包含首页、消息、设置三个 tab；首页“查看消息”按钮跳转到消息 tab。
- 2026-07-13：消息 tab 改为列表页，从 `mock/msgList.json` 打包资源读取假数据并展示。
- 2026-07-13：新增消息详情页；点击消息列表 item 会打开详情页显示完整内容。消息列表 item 增加高度限制，内容最多显示 2 行，超出用省略号截断。
- 2026-07-13：将解决方案重命名为 `HxPush`，并把项目文件夹/项目名改为 `HxPushApp`；同步更新命名空间、XAML `x:Class`、Windows manifest 和项目标识。 
- 2026-07-13：将 `HxPushApp` 配置为 Android-only；移除 iOS、MacCatalyst、Windows 目标框架和 Windows 设计器用户配置残留，平台文件夹暂保留但不参与目标框架编译。
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
- 2026-07-15：AppKey 输入框增加完成事件；Android 键盘点“完成”或桌面按 Enter 时自动校验并保存，保存按钮复用同一逻辑，成功后显示短暂页面 Toast。
- 2026-07-15：设置页新增服务器地址下拉框，提供局域网、正式地址和备用地址三个选项；选择后立即通过 `AppSettings` 保存并显示 Toast，已有连接会断开，后续测试连接和消息发送均使用当前选中的 WebSocket URI。
- 2026-07-15：消息页为 Windows 增加标题右侧刷新按钮，解决桌面端无法使用触摸下拉手势的问题；Windows 按钮与 Android 下拉刷新共用同一刷新流程，加载期间禁用重复操作并显示刷新状态。
- 2026-07-15：移除设置页 `OnDisappearing` 中的自动 WebSocket 断开，避免切换 Shell Tab 时中断连接；连接现在只在用户主动断开、切换服务器或发生网络/服务端异常时结束。
- 2026-07-15：消息首批查询和分页查询继续在 SQLite 使用 `MsgDate DESC, ID DESC`，页面装载每批结果时也显式执行相同降序排序，确保最新消息始终显示在列表顶部且同秒消息顺序稳定。
- 2026-07-15：本地 SQLite 消息限制为最新 10000 条，数据库初始化和每次写入后都会删除超量旧记录；WebSocket 提升为应用级共享连接，App 启动时初始化数据库并自动连接，未真正保存 AppKey 时跳转设置页，保存 AppKey 后自动连接。
- 2026-07-15：共享 `HxPushMsgModel` 增加 `IsRead` 后，本地 SQLite 表同步增加兼容迁移和查询字段，避免新版在线推送消息保存时因旧表缺列失败。
- 2026-07-15：`MsgDate` 从秒级 `int` 升级为毫秒级 `long`，本地旧数据初始化时自动乘以 1000；消息列表和详情时间显示到毫秒，WebSocket 缺省时间与测试消息同步使用 Unix 毫秒。
- 2026-07-15：消息时间统一以服务端返回的 `MsgDate` 为准；HxPushApp 发送测试消息时不再生成时间，接收消息要求服务端提供有效 `MsgDate`，本地存储、分页和显示继续只使用该字段。
- 2026-07-15：新增应用级 `HttpClientHelper`，复用单一 `HttpClient`，支持文本 GET、泛型 JSON GET/POST、自定义请求头和请求方法、取消令牌，并在非成功状态下返回包含状态码与响应正文的异常信息，为后续 HTTP 拉取消息提供基础。
- 2026-07-15：消息列表在设备信息行右侧显示消息本地时分秒，消息详情增加独立时间字段；两处统一通过 Unix 时间转换器将 `MsgDate` 格式化为 `HH:mm:ss`，无效时间显示占位符。
- 2026-07-15：消息列表和详情的时间格式补充年月日，统一调整为本地时间 `yyyy-MM-dd HH:mm:ss`。
- 2026-07-15：消息列表移除消息 ID，毫秒级 `MsgDate` 仍用于精确排序但显示格式去掉毫秒；新增设备 ID 下拉筛选，设备列表来自 SQLite 去重查询，首批加载和滚动分页都在数据库层应用相同 Hwid 条件，并增加组合索引优化筛选性能。
- 2026-07-15：WebSocket 接收处理同时兼容实时单条 `HxPushMsgModel` 和连接后补发的 `HxPushMsgModel[]` 未读数组；补发列表批量写入本地 SQLite，相同消息 ID 覆盖保存以保证服务端重试时幂等。
- 2026-07-15：消息页下拉刷新会先通过 `HxPushMessageApiClient` 从服务端拉取最新 50 条并写入 SQLite，再从本地库展示合并结果；向下滚动到底且本地没有更旧数据时，会按最后一条消息的 `MsgDate + ID` 游标向服务端再拉最多 50 条，WebSocket、HTTP 拉取和 SQLite 存储继续分层解耦。
- 2026-07-15：修复 Android manifest 图标引用，将 `@mipmap/appicon` 对齐为当前 MAUI 图标资源 `@mipmap/iconhx`，恢复 Android 构建通过。
- 2026-07-15：优化消息页服务端同步体验；`HxPushMessageApiClient` 为消息拉取增加 10 秒超时，`HttpClientHelper.GetJsonAsync` 改为先读取完整文本再反序列化，避免部分平台卡在响应流读取；消息页将远端拉取和 SQLite 写入放到后台任务，并新增同步中的 loading 遮罩与超时提示。
- 2026-07-15：Android 已有 `INTERNET` 权限；为解决 `http://` / `ws://` 明文服务地址刷新时报 `connection failure`，新增 `Platforms/Android/Resources/xml/network_security_config.xml`，仅对白名单服务器 `192.168.31.119`、`43.142.82.217`、`push.huixingfifa.top` 放行明文流量，并在 manifest 中引用。
- 2026-07-15：按调试便利需求将 Android 明文网络配置改为全局允许所有 `http://` / `ws://` 访问，同时保留 `INTERNET` 权限；消息时间显示改为先按设备本地时区转换服务端 UTC 毫秒时间戳，再按今天、N 天前、超过 30 天显示年月日的规则格式化。
- 2026-07-15：`PushConnectionService` 在 WebSocket 推送成功写入 SQLite 后发布消息事件；消息页订阅该事件并在主线程按 `MsgDate + ID` 倒序、去重插入当前列表，使已打开的列表即时显示新推送并补充新的设备筛选项。
- 2026-07-15：消息页新增“有最新消息”浮层提示；用户不在列表顶部时收到并插入实时推送会显示该提示，滚动至第一条消息或手动刷新时自动隐藏。
- 2026-07-15：为消息页“有最新消息”浮层设置显式高 `ZIndex`，确保它覆盖原生 `RefreshView`/`CollectionView` 的滚动内容，不会被列表遮挡。
- 2026-07-15：最新消息提示改为只要 WebSocket 成功接收并保存任意推送就显示，不再受当前设备筛选、消息是否新增或列表滚动位置限制；用户滚动到顶部后仍会隐藏。
- 2026-07-15：消息页“有最新消息”浮层支持点击，点击后平滑回到消息列表顶部并隐藏提示；页面订阅应用级 WebSocket 连接状态事件，顶部状态标签实时显示已连接或“已断开与服务器的连接”。
- 2026-07-15：应用视觉主题调整为蓝色系：更新主色、按钮渐变、Shell TabBar 与浅蓝页面底色；首页、消息页和详情页使用蓝色渐变头图、圆角半透明卡片、阴影与统一信息层级，保持既有交互不变。
- 2026-07-15：为 Android 消息列表优化滑动性能：消息卡片改为固定 `HeightRequest`，`CollectionView` 使用 `MeasureFirstItem` 复用测量结果。通过已连接真机的固定短距离往返滑动复测，超帧从 5.29% 降至 0.14%，90 分位帧时从 19ms 降至 12ms；保留渐变与阴影视觉效果。
- 2026-07-15：消息列表卡片正文与元信息网格使用 `*,Auto` 行定义；当正文只有一行时，正文行会填满剩余空间，设备与日期信息稳定贴齐卡片底部。
- 2026-07-15：设置页新增“删除本地缓存”按钮，二次确认后会关闭并删除 SQLite 数据库及 WAL/SHM 辅助文件；消息页订阅删除事件并同步清空内存列表与设备筛选项。
- 2026-07-16：将 App 内 HTTP 请求改为使用 `HxPushSdk.HxPushWebApiClient`。`HxPushMessageApiClient` 仅保留 AppSettings 配置绑定、10 秒超时与服务器地址变更时的客户端复用；删除 `HttpClientHelper.cs` 与重复的消息反序列化/游标类型；`MessagesPage` 使用 SDK 的 `HxPushMessageCursor`；`HxPushApp.csproj` 增加对 `HxPushSdk` 的项目引用。
- 2026-07-16：HTTP 消息拉取增加 WebSocket 连通前置条件；`HxPushMessageApiClient.GetMessagesAsync` 在 `PushConnectionService.IsConnected` 为 false 时立即抛出“未连接到服务器”，不再发 REST；`MessagesPage` 同步/加载更多失败时对该文案做直出展示。
- 2026-07-16：修复“未连接到服务器”提示一闪而过：未连接时不再闪 loading 遮罩；错误改为底部 StatusToast 展示（约 3.2 秒），SummaryText 仍保留文案；Toast 在刷新指示器结束后再弹出，避免与下拉刷新/loading 重叠。
- 2026-07-16：消息页设备筛选 Picker 提高可读性：外层与内层边框使用蓝色描边，标题/选中文字使用深蓝与深色文本，背景改为浅蓝底，避免手机上文字和边框看不清。
- 2026-07-16：修复启动图配置错误。`MauiSplashScreen` 不能使用 `ForegroundFile`（仅 `MauiIcon` 支持背景+前景）；图标与启动图也不能共用同一 drawable 名 `iconhx`，否则 Android 出现 APT2260 `drawable/iconhx not found`。现改为 `MauiIcon` 用 `Resources/AppIcon/iconhx.png`，`MauiSplashScreen` 用 `Resources/Splash/iconhx_splash.png`。
- 2026-07-16：交互优化：去掉首页 Tab；首次无 AppKey 进入设置；设置页“测试连接”改为“开始连接”，连接成功跳转消息；每次打开 APP 自动连接一次，成功进消息、失败进设置。
- 2026-07-16：修复 VS 发布“创建应用存档失败 / Android 存档无效（不是 .apk）”：Android 目标统一 `AndroidPackageFormat=apk`（避免默认 aab 被存档管理器当成无效包）；`ApplicationId` 与 manifest package `com.huix.push` 对齐。本机可用 `dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=apk` 产出 APK。
- 2026-07-16：统一 Android 包名为 `com.huix.push`（`HxPushApp.csproj` 的 `ApplicationId` 与 `Platforms/Android/AndroidManifest.xml` 的 `package`），替换原 `com.huix.jb` / `com.huixing.push` / `com.companyname.hxpushapp`。
- 2026-07-16：为本对话约定默认只处理 `HxPushServerWeb`；在 `HxPushServerWeb/run-linux.sh` 增加 Linux 后台启停脚本（start/stop/restart/status/log，默认端口 5212）。
- 2026-07-16：`HxPushServerWeb` 静态文件支持 `.apk` 下载（补 MIME + ServeUnknownFileTypes），避免 `wwwroot/1.apk` 404。
- 2026-07-16：`HxPushServerWeb` 新增消息管理页 `msgManager.html` 与独立 `HxPushMessageAdminHandler`（`/api/admin/messages*`）；支持 `sort=desc|asc` 按时间排序，默认倒序。
- 2026-07-16：修复 Release APK 无法安装 `INSTALL_PARSE_FAILED_NO_CERTIFICATES`：Release 未签名只会产出无 `-Signed` 的 apk。已为 Android Release 默认使用 debug.keystore 签名；正式证书可通过本地 `HxPushApp.Signing.props` 覆盖。安装时请用 `*-Signed.apk`，不要装未签名的 `.apk`。
- 2026-07-17：修复真机打开即闪退。Android 覆盖 MAUI 默认 DayNight 为主题 `Light.DarkActionBar`，底部导航强制 TextAppearance；`ModeNightNo` 提前到 Application 静态构造与 Activity.OnCreate；补 `colorActionMenuTextColor`；Android 不在 App 构造设置 `UserAppTheme`。
