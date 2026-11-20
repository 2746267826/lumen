using System.Globalization;

namespace Lumen.Windows.Infrastructure;

public static class Formatters
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.##} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : $"{Bytes((long)bytesPerSecond)}/s";

    public static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string FullTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public static string RelativeTime(DateTimeOffset value)
    {
        var span = DateTimeOffset.UtcNow - value.ToUniversalTime();
        if (span.TotalSeconds < 45)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span.TotalHours < 24)
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        if (span.TotalDays < 7)
        {
            return $"{(int)span.TotalDays} 天前";
        }

        return value.ToLocalTime().ToString("MM-dd", CultureInfo.CurrentCulture);
    }
}
