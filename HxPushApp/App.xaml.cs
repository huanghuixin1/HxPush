using Microsoft.Extensions.DependencyInjection;

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

            if (!Helpers.AppSettings.HasAppKey)
            {
                await shell.GoToAsync("//SettingsPage");
                return;
            }

            try
            {
                await Helpers.PushConnectionService.Instance.ConnectAsync();
            }
            catch
            {
                // 启动连接失败时保留应用可用性，用户可在设置页重新测试连接。
            }
        }
    }
}
