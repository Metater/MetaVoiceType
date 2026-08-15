using MetaVoiceType.Core.Interfaces;
using SharpHook;
using SharpHook.Data;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly EventLoopGlobalHook _hook = new(GlobalHookType.Keyboard);
    private bool _control;
    private Task? _run;
    public event EventHandler? ToggleRecording;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _hook.KeyPressed += OnPressed;
        _hook.KeyReleased += OnReleased;
        _run = _hook.RunAsync();
        return Task.CompletedTask;
    }

    private void OnPressed(object? sender, KeyboardHookEventArgs args)
    {
        if (args.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _control = true;
        else if (_control && args.Data.KeyCode == KeyCode.VcSpace && !args.IsEventSimulated) ToggleRecording?.Invoke(this, EventArgs.Empty);
    }
    private void OnReleased(object? sender, KeyboardHookEventArgs args)
    {
        if (args.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _control = false;
    }

    public async ValueTask DisposeAsync()
    {
        _hook.KeyPressed -= OnPressed; _hook.KeyReleased -= OnReleased;
        if (_hook.IsRunning) _hook.Stop();
        if (_run is not null) try { await _run.ConfigureAwait(false); } catch (HookException) { }
        _hook.Dispose();
    }
}
