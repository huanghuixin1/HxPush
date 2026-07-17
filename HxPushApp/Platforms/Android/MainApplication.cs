using Android.App;
using Android.Runtime;
using AndroidX.AppCompat.App;

namespace HxPushApp
{
    [Application]
    public class MainApplication : MauiApplication
    {
        static MainApplication()
        {
            // 静态构造最早执行：在任何 Activity/主题解析前固定浅色模式。
            // 若放在 base.OnCreate 之后，部分真机会先按 DayNight 解析 Shell 底部导航并闪退。
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightNo;
        }

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        public override void OnCreate()
        {
            // 再次确保：某些 OEM 在 Application 构造与 OnCreate 之间会重置 night mode。
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightNo;
            base.OnCreate();
        }
    }
}
