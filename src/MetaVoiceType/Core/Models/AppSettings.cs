namespace MetaVoiceType.Core.Models;

public enum AppTheme { Dark, Light, System }

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool OnboardingComplete { get; init; }
    public bool StartWithWindows { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public string VoiceCommandLanguage { get; init; } = "en-us";
    public string DictationLanguage { get; init; } = "auto";
    public string? AudioDeviceId { get; init; }
    public bool CopyOnStop { get; init; } = true;
    public bool ShowFloatingPill { get; init; } = true;
    public double CueVolume { get; init; } = 0.6;
    public string ToggleHotkey { get; init; } = "Ctrl+Space";
    public bool CloseToTrayNoticeShown { get; init; }
    public Dictionary<string, Dictionary<string, string>> CommandOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
