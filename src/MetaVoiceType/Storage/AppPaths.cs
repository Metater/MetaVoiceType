namespace MetaVoiceType.Storage;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("METAVOICETYPE_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetaVoiceType");
        PreferencesRoot = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaVoiceType");
    }

    public string Root { get; }
    public string PreferencesRoot { get; }
    public string SettingsFile => Path.Combine(PreferencesRoot, "settings.json");
    public string LegacySettingsFile => Path.Combine(Root, "settings.json");
    public string HistoryFile => Path.Combine(Root, "history.json");
    public string Models => Path.Combine(Root, "Models");
    public string DictationModels => Path.Combine(Models, "Parakeet");
    public string RuntimeModels => Path.Combine(Models, "Runtime");
    public string VoskModels => Path.Combine(Models, "Vosk");
    public string Recovery => Path.Combine(Root, "Recovery");
    public string Logs => Path.Combine(Root, "Logs");

    public void EnsureCreated()
    {
        foreach (string path in new[] { Root, PreferencesRoot, Models, DictationModels, RuntimeModels, VoskModels, Recovery, Logs })
            Directory.CreateDirectory(path);
    }
}
