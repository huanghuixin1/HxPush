using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.AppCompat.App;

namespace HxPushApp
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize
            | ConfigChanges.Orientation
            | ConfigChanges.UiMode
            | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize
            | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Activity 主题应用前再固定一次，覆盖系统深色/强制深色带来的 uiMode 抖动。
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightNo;
            base.OnCreate(savedInstanceState);
        }

        protected override void OnDestroy()
        {
            // 常规关闭时尽力完成关闭握手；Android 强行停止进程不保证回调，因此服务端不能依赖此处判断已读。
            try
            {
                Task.Run(() => Helpers.PushConnectionService.Instance.DisconnectAsync())
                    .Wait(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // 退出路径不能因网络关闭失败阻止 Activity 销毁。
            }

            base.OnDestroy();
        }
    }
}
