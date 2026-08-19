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

    public Task SendEnterAsync(CancellationToken cancellationToken = default) =>
        SendShortcutAsync(new(false, false, false, false, KeyCode.VcEnter), cancellationToken);

    public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shortcut.IsMouse)
        {
            PressModifiers(shortcut);
            try
            {
                EnsureSuccess(_simulator.SimulateMousePress(shortcut.MouseButton), shortcut.MouseButton, "press");
                EnsureSuccess(_simulator.SimulateMouseRelease(shortcut.MouseButton), shortcut.MouseButton, "release");
            }
            finally { ReleaseModifiers(shortcut); }
            return Task.CompletedTask;
        }
        var pressed = new List<KeyCode>();
        try
        {
            foreach (KeyboardStroke stroke in shortcut.PlaybackSequence())
            {
                if (stroke.IsKeyDown)
                {
                    EnsureSuccess(_simulator.SimulateKeyPress(stroke.Key), stroke.Key, "press");
                    pressed.Add(stroke.Key);
                }
                else
                {
                    EnsureSuccess(_simulator.SimulateKeyRelease(stroke.Key), stroke.Key, "release");
                    pressed.Remove(stroke.Key);
                }
            }
        }
        finally
        {
            for (int i = pressed.Count - 1; i >= 0; i--) _simulator.SimulateKeyRelease(pressed[i]);
        }
        return Task.CompletedTask;
    }

    public Task PressShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shortcut.IsMouse)
        {
            PressModifiers(shortcut);
            try { EnsureSuccess(_simulator.SimulateMousePress(shortcut.MouseButton), shortcut.MouseButton, "press"); }
            catch { ReleaseModifiers(shortcut); throw; }
            return Task.CompletedTask;
        }
        var pressed = new List<KeyCode>();
        try
        {
            foreach (KeyCode key in shortcut.Modifiers.Append(shortcut.Key))
            {
                EnsureSuccess(_simulator.SimulateKeyPress(key), key, "press");
                pressed.Add(key);
            }
        }
        catch
        {
            for (int i = pressed.Count - 1; i >= 0; i--) _simulator.SimulateKeyRelease(pressed[i]);
            throw;
        }
        return Task.CompletedTask;
    }

    public Task ReleaseShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shortcut.IsMouse)
        {
            EnsureSuccess(_simulator.SimulateMouseRelease(shortcut.MouseButton), shortcut.MouseButton, "release");
            ReleaseModifiers(shortcut);
            return Task.CompletedTask;
        }
        foreach (KeyCode key in shortcut.Modifiers.Append(shortcut.Key).Reverse())
            EnsureSuccess(_simulator.SimulateKeyRelease(key), key, "release");
        return Task.CompletedTask;
    }

    private static void EnsureSuccess(UioHookResult result, KeyCode key, string action)
    {
        if (result != UioHookResult.Success) throw new InvalidOperationException($"SharpHook could not {action} {key}: {result}.");
    }

    private static void EnsureSuccess(UioHookResult result, MouseButton button, string action)
    {
        if (result != UioHookResult.Success) throw new InvalidOperationException($"SharpHook could not {action} {button}: {result}.");
    }

    private void PressModifiers(ShortcutGesture shortcut)
    {
        foreach (KeyCode key in shortcut.Modifiers) EnsureSuccess(_simulator.SimulateKeyPress(key), key, "press");
    }

    private void ReleaseModifiers(ShortcutGesture shortcut)
    {
        foreach (KeyCode key in shortcut.Modifiers.Reverse()) _simulator.SimulateKeyRelease(key);
    }
}
