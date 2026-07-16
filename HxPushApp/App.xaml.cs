namespace HxPushApp
{
    public partial class App : Application
    {
        private bool isStartupInitialized;

        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = new AppShell();
            var window = new Window(shell);
            window.Created += async (_, _) => await InitializeStartupAsync(shell);
            return window;
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
