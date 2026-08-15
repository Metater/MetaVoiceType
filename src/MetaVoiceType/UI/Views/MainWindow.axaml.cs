using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.UI.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (DataContext is ViewModels.MainViewModel { ShouldShowCloseToTrayNotice: true } firstVm)
            {
                new TrayNoticeWindow().Show();
                _ = firstVm.MarkCloseToTrayNoticeShownAsync();
            }
            Hide();
            if (DataContext is ViewModels.MainViewModel vm) vm.State.StatusMessage = "MetaVoiceType is still listening in the system tray.";
        };
    }

    public void ExitApplication()
    {
        _allowClose = true;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.TryShutdown();
    }

    private void CopyHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptRecord record } && DataContext is ViewModels.MainViewModel vm)
            vm.CopyHistoryCommand.Execute(record);
    }

    private void PasteHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptRecord record } && DataContext is ViewModels.MainViewModel vm)
            vm.PasteHistoryCommand.Execute(record);
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm || (!vm.IsCapturingHotkey && !vm.IsCapturingCustomShortcut)) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        string gesture = FormatGesture(e);
        e.Handled = true;
        if (vm.IsCapturingCustomShortcut) vm.CaptureCustomShortcut(gesture);
        else await vm.CaptureHotkeyAsync(gesture);
    }

    private static string FormatGesture(KeyEventArgs e)
    {
        var parts = new List<string>(5);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(e.Key switch
        {
            Key.Space => "Space", Key.Return => "Enter", Key.Escape => "Escape", Key.Back => "Backspace",
            Key.Prior => "PageUp", Key.Next => "PageDown", _ => e.Key.ToString()
        });
        return string.Join('+', parts);
    }
}
