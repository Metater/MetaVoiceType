using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Integrations;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class CommandsAndIntegrationTests
{
    [Theory]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("Control+Shift+F9", "Ctrl+Shift+F9")]
    [InlineData("Win+Alt+P", "Alt+Win+P")]
    public void ShortcutParserProducesCanonicalSafeGestures(string input, string expected) =>
        Assert.Equal(expected, ShortcutGestureParser.Parse(input).ToString());

    [Theory]
    [InlineData("Space")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A+B")]
    public void ShortcutParserRejectsUnsafeGestures(string input) => Assert.Throws<FormatException>(() => ShortcutGestureParser.Parse(input));

    [Fact]
    public void CustomCommandsAreLanguageScopedAndCannotShadowBuiltIns()
    {
        var first = new CustomVoiceCommand { Id = "one", Name = "One", VoiceCommandLanguageId = "en-us", Phrase = "open editor", CommandType = CustomCommandType.KeyboardShortcut, Shortcut = "Ctrl+E" };
        var samePhraseOtherLanguage = new CustomVoiceCommand { Id = "two", Name = "Two", VoiceCommandLanguageId = "de", Phrase = "open editor", CommandType = CustomCommandType.KeyboardShortcut, Shortcut = "Ctrl+E" };
        CustomCommandValidator.Validate(first, ["start recording"], [first, samePhraseOtherLanguage]);
        first.Phrase = "start recording";
        Assert.Throws<InvalidDataException>(() => CustomCommandValidator.Validate(first, ["start recording"], [first, samePhraseOtherLanguage]));
    }

    [Theory]
    [InlineData(CustomCommandType.PowerShell, "Write-Output 'powershell-ok'", "powershell-ok")]
    [InlineData(CustomCommandType.CommandPrompt, "echo command-prompt-ok", "command-prompt-ok")]
    public async Task ShellCustomCommandsExecuteThroughManagedProcessApi(CustomCommandType type, string script, string expected)
    {
        var executor = new CustomCommandExecutor(new FakeInput(), NullLogger<CustomCommandExecutor>.Instance);
        var command = new CustomVoiceCommand { Id = "shell", Name = "Shell", Phrase = "run shell", CommandType = type, ScriptOrCommand = script };
        CustomCommandExecution result = await executor.ExecuteAsync(command, true, TestContext.Current.CancellationToken);
        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProgramAndKeyboardShortcutCustomCommandsExecute()
    {
        var input = new FakeInput();
        var executor = new CustomCommandExecutor(input, NullLogger<CustomCommandExecutor>.Instance);
        var program = new CustomVoiceCommand { Id = "program", Name = "Program", Phrase = "run program", CommandType = CustomCommandType.Program,
            Executable = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", Arguments = "/D /C echo program-ok" };
        CustomCommandExecution process = await executor.ExecuteAsync(program, true, TestContext.Current.CancellationToken);
        Assert.Contains("program-ok", process.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var shortcut = new CustomVoiceCommand { Id = "shortcut", Name = "Shortcut", Phrase = "send shortcut", CommandType = CustomCommandType.KeyboardShortcut, Shortcut = "Ctrl+Shift+K" };
        await executor.ExecuteAsync(shortcut, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Ctrl+Shift+K", input.Last?.ToString());
    }

    [Fact]
    public async Task DiscordMuteSpansOverlappingSessionTransitionsAndPreservesPriorMute()
    {
        var discord = new FakeDiscord();
        using var coordinator = new DiscordAutoMuteCoordinator(discord, NullLogger<DiscordAutoMuteCoordinator>.Instance);
        CancellationToken token = TestContext.Current.CancellationToken;
        await coordinator.RecordingStartedAsync("a", true, token);
        await coordinator.RecordingStartedAsync("b", true, token);
        await coordinator.RecordingEndedAsync("a", token);
        Assert.True(discord.Muted);
        await coordinator.RecordingEndedAsync("b", token);
        Assert.False(discord.Muted);
        Assert.Equal([true, false], discord.Changes);

        discord.Muted = true;
        await coordinator.RecordingStartedAsync("c", true, token);
        await coordinator.RecordingEndedAsync("c", token);
        Assert.True(discord.Muted);
        Assert.Equal([true, false], discord.Changes);
    }

    private sealed class FakeDiscord : IDiscordVoiceIntegration
    {
        public bool IsAvailable => true;
        public bool IsAuthorized => true;
        public string Status => "Connected";
        public bool Muted { get; set; }
        public List<bool> Changes { get; } = [];
        public event EventHandler<bool>? MuteStateChanged;
        public Task ConfigureAsync(string? clientId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Muted);
        public Task SetMuteStateAsync(bool muted, CancellationToken cancellationToken = default) { Muted = muted; Changes.Add(muted); MuteStateChanged?.Invoke(this, muted); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeInput : IKeyboardInputSimulator
    {
        public ShortcutGesture? Last { get; private set; }
        public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default) { Last = shortcut; return Task.CompletedTask; }
    }
}
