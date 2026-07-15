using Microsoft.Maui.Storage;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 集中管理应用的本地键值配置。
    /// 新增配置时在此处增加键名和强类型属性，避免页面散落存储键字符串。
    /// </summary>
    public static class AppSettings
    {
        public const string DefaultAppKey = "2222";

        private static class Keys
        {
            public const string AppKey = "settings.app_key";
            public const string DeviceId = "settings.device_id";
        }

        public static string AppKey
        {
            get => Preferences.Default.Get(Keys.AppKey, DefaultAppKey);
            set => Preferences.Default.Set(Keys.AppKey, value);
        }

        /// <summary>
        /// 当前安装实例的稳定设备标识。首次读取时生成，后续从本地配置复用。
        /// </summary>
        public static string DeviceId
        {
            get
            {
                var deviceId = Preferences.Default.Get(Keys.DeviceId, string.Empty);
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    return deviceId;
                }

                deviceId = Guid.NewGuid().ToString("N");
                Preferences.Default.Set(Keys.DeviceId, deviceId);
                return deviceId;
            }
        }
    }
}
