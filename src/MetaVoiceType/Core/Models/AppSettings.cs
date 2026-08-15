namespace MetaVoiceType.Core.Models;

public enum AppTheme { Dark, Light, System }
public enum DictationMode { Automatic, English }
public enum CustomCommandType { Program, PowerShell, CommandPrompt, KeyboardShortcut }
public enum CommandWindowMode { Normal, Minimized, Hidden }

public sealed class CustomVoiceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New command";
    public string VoiceCommandLanguageId { get; set; } = "en-us";
    public string Phrase { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CustomCommandType CommandType { get; set; }
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string ScriptOrCommand { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public CommandWindowMode WindowMode { get; set; }
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public bool OnboardingComplete { get; init; }
    public bool StartWithWindows { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public string VoiceCommandLanguage { get; init; } = "en-us";
    public DictationMode DictationMode { get; init; } = DictationMode.Automatic;
    public string? AudioDeviceId { get; init; }
    public bool CopyOnStop { get; init; } = true;
    public bool ShowFloatingPill { get; init; } = true;
    public double CueVolume { get; init; } = 0.6;
    public string ToggleHotkey { get; init; } = "Ctrl+Space";
    public bool MuteDiscordWhileRecording { get; init; }
    public string? DiscordClientId { get; init; }
    public bool CloseToTrayNoticeShown { get; init; }
    public Dictionary<string, Dictionary<string, string>> CommandOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomVoiceCommand> CustomCommands { get; init; } = [];
}
