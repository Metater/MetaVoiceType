using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Sessions;

public sealed class RecordingEventShortcutPlayer(IKeyboardInputSimulator input)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShortcutGesture?> _active = new(StringComparer.Ordinal);

    public Task RecordingStartedAsync(string segmentId, string? shortcut, CancellationToken cancellationToken = default) =>
        RecordingStartedAsync(segmentId, shortcut, null, cancellationToken);

    public async Task RecordingStartedAsync(string segmentId, string? shortcut, string? heldShortcut, CancellationToken cancellationToken = default)
    {
        ShortcutGesture? held = string.IsNullOrWhiteSpace(heldShortcut) ? null : ShortcutGestureParser.ParseAction(heldShortcut);
        lock (_gate) if (!_active.TryAdd(segmentId, held)) return;
        try
        {
            if (held is not null) await input.PressShortcutAsync(held, cancellationToken).ConfigureAwait(false);
            await PlayAsync(shortcut, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _active.Remove(segmentId);
            if (held is not null) await input.ReleaseShortcutAsync(held, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordingEndedAsync(string segmentId, string? shortcut, CancellationToken cancellationToken = default)
    {
        ShortcutGesture? held;
        lock (_gate) if (!_active.Remove(segmentId, out held)) return;
        if (held is not null) await input.ReleaseShortcutAsync(held, CancellationToken.None).ConfigureAwait(false);
        await PlayAsync(shortcut, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAllAsync()
    {
        ShortcutGesture[] held;
        lock (_gate) { held = _active.Values.OfType<ShortcutGesture>().ToArray(); _active.Clear(); }
        foreach (ShortcutGesture shortcut in held) await input.ReleaseShortcutAsync(shortcut, CancellationToken.None).ConfigureAwait(false);
    }

    private Task PlayAsync(string? shortcut, CancellationToken cancellationToken) => string.IsNullOrWhiteSpace(shortcut)
        ? Task.CompletedTask
        : input.SendShortcutAsync(ShortcutGestureParser.ParseAction(shortcut), cancellationToken);
}
