using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using SharpHook;
using SharpHook.Data;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly object _gate = new();
    private readonly EventLoopGlobalHook _hook = new(GlobalHookType.Keyboard);
    private readonly HashSet<KeyCode> _pressed = [];
    private ShortcutGesture _gesture = ShortcutGestureParser.Parse("Ctrl+Space");
    private Task? _run;
    private bool _triggerHeld;
    public event EventHandler? ToggleRecording;
    public string ActiveGesture { get { lock (_gate) return _gesture.ToString(); } }

    public Task StartAsync(string gesture = "Ctrl+Space", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _gesture = ShortcutGestureParser.Parse(gesture);
            if (_run is not null) return Task.CompletedTask;
            _hook.KeyPressed += OnPressed;
            _hook.KeyReleased += OnReleased;
            _run = _hook.RunAsync();
        }
        return Task.CompletedTask;
    }

    public Task<HotkeyChangeResult> ChangeAsync(string gesture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ShortcutGesture replacement = ShortcutGestureParser.Parse(gesture);
            lock (_gate) { _gesture = replacement; _triggerHeld = false; }
            return Task.FromResult(new HotkeyChangeResult(true, replacement.ToString()));
        }
        catch (FormatException ex) { return Task.FromResult(new HotkeyChangeResult(false, ActiveGesture, ex.Message)); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? run;
        lock (_gate)
        {
            run = _run;
            if (run is null) return;
            _hook.KeyPressed -= OnPressed;
            _hook.KeyReleased -= OnReleased;
            if (_hook.IsRunning) _hook.Stop();
            _run = null;
            _pressed.Clear();
            _triggerHeld = false;
        }
        try { await run.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (HookException) { }
    }

    private void OnPressed(object? sender, KeyboardHookEventArgs args)
    {
        if (args.IsEventSimulated) return;
        bool fire = false;
        lock (_gate)
        {
            _pressed.Add(args.Data.KeyCode);
            if (args.Data.KeyCode == _gesture.Key && !_triggerHeld && ModifiersMatch(_gesture))
            {
                _triggerHeld = true;
                fire = true;
            }
        }
        if (fire) ToggleRecording?.Invoke(this, EventArgs.Empty);
    }

    private void OnReleased(object? sender, KeyboardHookEventArgs args)
    {
        lock (_gate)
        {
            _pressed.Remove(args.Data.KeyCode);
            if (args.Data.KeyCode == _gesture.Key) _triggerHeld = false;
        }
    }

    private bool ModifiersMatch(ShortcutGesture value) =>
        HasEither(KeyCode.VcLeftControl, KeyCode.VcRightControl) == value.Control &&
        HasEither(KeyCode.VcLeftShift, KeyCode.VcRightShift) == value.Shift &&
        HasEither(KeyCode.VcLeftAlt, KeyCode.VcRightAlt) == value.Alt &&
        HasEither(KeyCode.VcLeftMeta, KeyCode.VcRightMeta) == value.Windows;
    private bool HasEither(KeyCode left, KeyCode right) => _pressed.Contains(left) || _pressed.Contains(right);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _hook.Dispose();
    }
}
