using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MetaVoiceType.UI.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = value?.ToString()?.ToLowerInvariant() ?? "";
        string resource = text.Contains("busy", StringComparison.Ordinal) || text.Contains("downloading", StringComparison.Ordinal) ||
            text.Contains("initializing", StringComparison.Ordinal) || text.Contains("activating", StringComparison.Ordinal) ||
            text.Contains("preparing", StringComparison.Ordinal) || text.Contains("finalizing", StringComparison.Ordinal) || text.Contains("saving", StringComparison.Ordinal)
            ? "WarningBrush"
            : text.Contains("inactive", StringComparison.Ordinal) || text.Contains("not active", StringComparison.Ordinal) ||
              text.Contains("disabled", StringComparison.Ordinal) || text.Contains("not installed", StringComparison.Ordinal) ||
              text.Contains("unavailable", StringComparison.Ordinal) || text.Contains("failed", StringComparison.Ordinal) || text.Contains("error", StringComparison.Ordinal)
                ? "DangerBrush"
                : text.Contains("active", StringComparison.Ordinal) || text.Contains("ready", StringComparison.Ordinal) ||
                  text.Contains("installed", StringComparison.Ordinal) || text.Contains("up to date", StringComparison.Ordinal)
                    ? "SuccessBrush" : "TextBrush";
        return Application.Current?.Resources.TryGetResource(resource, Application.Current.ActualThemeVariant, out object? brush) == true ? brush : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
