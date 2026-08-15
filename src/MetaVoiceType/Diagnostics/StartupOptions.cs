using System.CommandLine;

namespace MetaVoiceType.Diagnostics;

public sealed record StartupOptions(bool SelfTest, bool ListAudioDevices, bool Diagnostics, bool ForceCpu, bool ResetOnboarding, bool InstallModels,
    string? AudioFile, string DictationLanguage)
{
    public bool ExitAfterDiagnostics => SelfTest || ListAudioDevices || InstallModels || AudioFile is not null || StressMinutes > 0 || PasteText is not null || RecoveryCrashSeconds > 0;

    public static StartupOptions Parse(string[] args)
    {
        var selfTest = new Option<bool>("--self-test") { Description = "Run local catalog, model, and audio checks, then exit." };
        var listAudio = new Option<bool>("--list-audio-devices") { Description = "List active Windows capture devices, then exit." };
        var diagnostics = new Option<bool>("--diagnostics") { Description = "Enable verbose local diagnostic logging." };
        var forceCpu = new Option<bool>("--force-cpu") { Description = "Force the managed transcription backend to use its CPU provider." };
        var resetOnboarding = new Option<bool>("--reset-onboarding") { Description = "Show onboarding again without deleting other settings." };
        var installModels = new Option<bool>("--install-models") { Description = "Download and verify the default command and dictation models, then exit." };
        var audioFile = new Option<FileInfo?>("--audio-file") { Description = "Run both recognizers against an audio file, then exit." };
        var dictationLanguage = new Option<string?>("--dictation-language") { Description = "Override Nemotron's diagnostic language (default: auto)." };
        var commandLanguage = new Option<string?>("--command-language") { Description = "Select a Vosk diagnostic language (default: en-us)." };
        var testCommand = new Option<bool>("--test-command") { Description = "Require the supplied audio file to emit a configured Vosk command." };
        var stressMinutes = new Option<int>("--stress-minutes") { Description = "Stream the default microphone through both recognizers for the specified duration." };
        var pasteText = new Option<string?>("--paste-text") { Description = "Paste exact diagnostic text into the focused application, then exit." };
        var recoveryCrashSeconds = new Option<int>("--recovery-crash-seconds") { Description = "Capture recovery audio for N seconds, then terminate abruptly for recovery testing." };
        var root = new RootCommand("MetaVoiceType local Windows dictation");
        root.Options.Add(selfTest); root.Options.Add(listAudio); root.Options.Add(diagnostics); root.Options.Add(forceCpu); root.Options.Add(resetOnboarding); root.Options.Add(installModels); root.Options.Add(audioFile); root.Options.Add(dictationLanguage); root.Options.Add(commandLanguage); root.Options.Add(testCommand); root.Options.Add(stressMinutes); root.Options.Add(pasteText); root.Options.Add(recoveryCrashSeconds);
        ParseResult result = root.Parse(args);
        if (result.Errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, result.Errors.Select(x => x.Message)));
        int minutes = result.GetValue(stressMinutes);
        if (minutes < 0) throw new ArgumentException("--stress-minutes cannot be negative.");
        int crashSeconds = result.GetValue(recoveryCrashSeconds);
        if (crashSeconds < 0) throw new ArgumentException("--recovery-crash-seconds cannot be negative.");
        return new(result.GetValue(selfTest), result.GetValue(listAudio), result.GetValue(diagnostics), result.GetValue(forceCpu), result.GetValue(resetOnboarding),
            result.GetValue(installModels), result.GetValue(audioFile)?.FullName, result.GetValue(dictationLanguage) ?? "auto")
        {
            CommandLanguage = result.GetValue(commandLanguage) ?? "en-us",
            TestCommand = result.GetValue(testCommand),
            StressMinutes = minutes,
            PasteText = result.GetValue(pasteText),
            RecoveryCrashSeconds = crashSeconds
        };
    }

    public string CommandLanguage { get; init; } = "en-us";
    public bool TestCommand { get; init; }
    public int StressMinutes { get; init; }
    public string? PasteText { get; init; }
    public int RecoveryCrashSeconds { get; init; }
}
