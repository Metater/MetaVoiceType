using Avalonia.Controls;
using Avalonia.Interactivity;

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
}
