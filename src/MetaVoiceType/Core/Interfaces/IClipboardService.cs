using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Core.Interfaces;

public interface IClipboardService { Task SetTextAsync(string text, CancellationToken cancellationToken = default); }
public interface ITextInsertionService
{
    Task PasteAsync(CancellationToken cancellationToken = default);
    Task SendEnterAsync(CancellationToken cancellationToken = default);
}
public interface IKeyboardInputSimulator
{
    Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default);
    Task PressShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default) => SendShortcutAsync(shortcut, cancellationToken);
    Task ReleaseShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public interface IStartupService { bool IsEnabled { get; } void SetEnabled(bool enabled); }
public sealed record HotkeyChangeResult(bool Success, string ActiveGesture, string? Error = null);
public interface IGlobalHotkeyService : IAsyncDisposable
{
    event EventHandler? ToggleRecording;
    string ActiveGesture { get; }
    Task StartAsync(string gesture = "Ctrl+Space", CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<HotkeyChangeResult> ChangeAsync(string gesture, CancellationToken cancellationToken = default);
}
