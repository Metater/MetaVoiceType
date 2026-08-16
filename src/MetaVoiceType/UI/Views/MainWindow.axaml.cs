using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MetaVoiceType.Core.Models;
using MetaVoiceType.UI.ViewModels;
using System.Diagnostics;

namespace MetaVoiceType.UI.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private IDisposable? _spectrumLease;
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _spectrumLease ??= AppServices.TryGet<Audio.AudioSpectrumService>()?.Acquire();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
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
            _spectrumLease?.Dispose(); _spectrumLease = null;
            if (DataContext is ViewModels.MainViewModel vm) vm.State.StatusMessage = "MetaVoiceType is still listening in the system tray.";
        };
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width < 820;
        LiveGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,300");
        Grid.SetColumn(CommandCard, narrow ? 0 : 1);
        Grid.SetRow(CommandCard, narrow ? 1 : 0);
    }

    public void ExitApplication()
    {
        _spectrumLease?.Dispose(); _spectrumLease = null;
        _allowClose = true;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.TryShutdown();
    }

    private void CopyHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptRecord record } && DataContext is ViewModels.MainViewModel vm)
            vm.CopyHistoryCommand.Execute(record);
    }

    private void DeleteHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptRecord record } && DataContext is ViewModels.MainViewModel vm)
            vm.RequestDeleteHistoryCommand.Execute(record);
    }

    private void DeleteReplacementClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReplacementGroupEditorViewModel replacement } && DataContext is ViewModels.MainViewModel vm)
            vm.DeleteWordReplacementCommand.Execute(replacement);
    }

    private void OpenLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm || vm.ActiveShortcutCapture == MainViewModel.ShortcutCaptureTarget.None) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        string gesture = FormatGesture(e);
        e.Handled = true;
        switch (vm.ActiveShortcutCapture)
        {
            case MainViewModel.ShortcutCaptureTarget.CustomCommand: vm.CaptureCustomShortcut(gesture); break;
            case MainViewModel.ShortcutCaptureTarget.RecordingStarted or MainViewModel.ShortcutCaptureTarget.RecordingStopped: vm.CaptureRecordingEventShortcut(gesture); break;
            case MainViewModel.ShortcutCaptureTarget.RecordingToggle: await vm.CaptureHotkeyAsync(gesture); break;
        }
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
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Scroll => "ScrollLock",
            Key.Pause => "Pause",
            _ => e.Key.ToString()
        });
        return string.Join('+', parts);
    }
}
