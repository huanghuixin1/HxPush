namespace HxPushApp
{
    public partial class App : Application
    {
        private bool isStartupInitialized;

        public App()
        {
            InitializeComponent();
#if WINDOWS
            // Windows 可直接固定 MAUI 浅色主题。
            // 切勿在 Android 上于 App 构造期设置 UserAppTheme：部分真机会让 Shell
            // Material 底部导航拿不到 TextAppearance，启动即闪退。
            UserAppTheme = AppTheme.Light;
#endif
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = new AppShell();
            var window = new Window(shell);
            window.Created += async (_, _) => await InitializeStartupAsync(shell);
            // 解锁/从后台回到前台：检查 WS 是否掉线并自动补连。
            window.Resumed += async (_, _) => await OnAppResumedAsync();
            window.Destroying += async (_, _) => await Helpers.PushConnectionService.Instance.DisconnectAsync();
            return window;
        }

        /// <summary>
        /// Android 等平台生命周期恢复（含锁屏后回到应用）。
        /// </summary>
        protected override void OnResume()
        {
            base.OnResume();
            _ = OnAppResumedAsync();
        }

        private static async Task OnAppResumedAsync()
        {
            try
            {
                await Helpers.PushConnectionService.Instance.EnsureConnectedAsync();
            }
            catch
            {
                // 前台补连失败由服务内部日志与退避重试处理，不打断 UI。
            }
        }

        /// <summary>
        /// 应用启动：初始化本地库，自动连接一次。
        /// 未配置 AppKey 或连接失败进入设置；连接成功进入消息。
        /// </summary>
        private async Task InitializeStartupAsync(AppShell shell)
        {
            if (isStartupInitialized)
            {
                return;
            }

            isStartupInitialized = true;

            try
            {
                await Helpers.Sqlite.SqliteHelper.Instance.InitializeAsync();
            }
            catch
            {
                // 数据库错误会在具体读写页面中反馈，不阻止应用启动和配置 AppKey。
            }

            // 首次打开（尚未保存 AppKey）直接进入设置页配置。
            if (!Helpers.AppSettings.HasAppKey)
            {
                await shell.GoToAsync("//SettingsPage");
                return;
            }

            // 每次打开应用自动连接一次。
            try
            {
                await Helpers.PushConnectionService.Instance.ConnectAsync();
                if (Helpers.PushConnectionService.Instance.IsConnected)
                {
                    await shell.GoToAsync("//MessagesPage");
                    return;
                }
            }
            catch
            {
                // 启动连接失败时进入设置页，便于用户手动开始连接。
            }

            await shell.GoToAsync("//SettingsPage");
        }
    }
}
