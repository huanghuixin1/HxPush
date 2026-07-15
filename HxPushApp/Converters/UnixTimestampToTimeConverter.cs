using System.Globalization;

namespace HxPushApp.Converters
{
    /// <summary>
    /// 将毫秒级 Unix 时间戳转换为设备本地日期时间。
    /// </summary>
    public sealed class UnixTimestampToTimeConverter : IValueConverter
    {
        public object Convert(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture)
        {
            var timestamp = value switch
            {
                int intValue => intValue,
                long longValue => longValue,
                _ => 0L
            };

            if (timestamp <= 0)
            {
                return "--:--:--";
            }

            try
            {
                return DateTimeOffset
                    .FromUnixTimeMilliseconds(timestamp)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", culture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "--:--:--";
            }
        }

        public object ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
