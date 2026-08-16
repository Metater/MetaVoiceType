using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Sessions;

public sealed class RecordingEventShortcutPlayer(IKeyboardInputSimulator input)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public Task RecordingStartedAsync(string segmentId, string? shortcut, CancellationToken cancellationToken = default)
    {
        lock (_gate) if (!_active.Add(segmentId)) return Task.CompletedTask;
        return PlayAsync(shortcut, cancellationToken);
    }

    public Task RecordingEndedAsync(string segmentId, string? shortcut, CancellationToken cancellationToken = default)
    {
        lock (_gate) if (!_active.Remove(segmentId)) return Task.CompletedTask;
        return PlayAsync(shortcut, cancellationToken);
    }

    private Task PlayAsync(string? shortcut, CancellationToken cancellationToken) => string.IsNullOrWhiteSpace(shortcut)
        ? Task.CompletedTask
        : input.SendShortcutAsync(ShortcutGestureParser.ParseAction(shortcut), cancellationToken);
}
