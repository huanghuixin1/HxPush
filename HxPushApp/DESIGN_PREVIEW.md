# HxPushApp 界面预览

MAUI 没有传统 WinForms 那种纯拖拽设计器。更省时间的做法是用 Windows 目标运行一次，然后配合 Visual Studio 的 XAML Live Preview / XAML Hot Reload 看实时效果。

## 推荐流程

1. 在 Visual Studio 顶部目标框选择 `net10.0-windows10.0.19041.0`。
2. 运行配置选择 `Windows Machine`。
3. 启动一次应用。
4. 打开 `调试 > 窗口 > XAML Live Preview`。
5. 修改 `.xaml` 文件后保存，界面会通过 Hot Reload 刷新。

这样改界面时不用反复部署 Android，只在需要确认真机效果时再切回 `net10.0-android`。

## 命令行验证

```powershell
dotnet build HxPushApp.csproj -f net10.0-windows10.0.19041.0
dotnet build HxPushApp.csproj -f net10.0-android
```
