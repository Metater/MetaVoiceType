using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using SharpHook;
using SharpHook.Data;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsTextInsertionService : ITextInsertionService, IKeyboardInputSimulator
{
    private readonly EventSimulator _simulator = new();

    public Task PasteAsync(CancellationToken cancellationToken = default) =>
        SendShortcutAsync(new(true, false, false, false, KeyCode.VcV), cancellationToken);

    public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pressed = new List<KeyCode>();
        try
        {
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                EnsureSuccess(_simulator.SimulateKeyPress(modifier), modifier, "press");
                pressed.Add(modifier);
            }
            EnsureSuccess(_simulator.SimulateKeyPress(shortcut.Key), shortcut.Key, "press");
            EnsureSuccess(_simulator.SimulateKeyRelease(shortcut.Key), shortcut.Key, "release");
        }
        finally
        {
            for (int i = pressed.Count - 1; i >= 0; i--) _simulator.SimulateKeyRelease(pressed[i]);
        }
        return Task.CompletedTask;
    }

    private static void EnsureSuccess(UioHookResult result, KeyCode key, string action)
    {
        if (result != UioHookResult.Success) throw new InvalidOperationException($"SharpHook could not {action} {key}: {result}.");
    }
}
