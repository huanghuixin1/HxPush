using System.Globalization;

namespace HxPushApp.Converters
{
    /// <summary>
    /// 将服务端返回的 UTC Unix 毫秒时间戳转换为设备本地时间显示。
    /// 今天显示“今天 HH:mm:ss”，1 到 30 天内显示“N天前 HH:mm:ss”，更早显示完整年月日。
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
                var localDateTime = DateTimeOffset
                    .FromUnixTimeMilliseconds(timestamp)
                    .ToLocalTime();
                var today = DateTime.Today;
                var messageDate = localDateTime.Date;
                var dayDiff = (today - messageDate).Days;
                var timeText = localDateTime.ToString("HH:mm:ss", culture);

                return dayDiff switch
                {
                    0 => $"今天 {timeText}",
                    >= 1 and <= 30 => $"{dayDiff}天前 {timeText}",
                    _ => localDateTime.ToString("yyyy-MM-dd HH:mm:ss", culture)
                };
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
