using System.Globalization;

namespace MetaVoiceType.Storage;

public static class TranscriptTimeFormatter
{
    public static string Format(DateTimeOffset utcValue, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utcValue.ToUniversalTime(), timeZone);
        TimeSpan offset = local.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{local.ToString("g", culture)} (UTC{sign}{offset.Hours:00}:{offset.Minutes:00})";
    }
}
