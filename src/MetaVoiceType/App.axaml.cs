using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.UI.Views;

namespace MetaVoiceType;

public sealed partial class App : Application
{
    private MainWindow? _mainWindow;
    private PillWindow? _pillWindow;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var viewModel = AppServices.Get<UI.ViewModels.MainViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            _mainWindow = window;
            _pillWindow = new PillWindow { DataContext = viewModel };
            _pillWindow.OpenMainWindowRequested += OpenFromTray;
            viewModel.State.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is not (nameof(viewModel.State.IsRecording) or nameof(viewModel.State.PasteState)) || _pillWindow is null) return;
                if ((viewModel.State.IsRecording || viewModel.State.IsPasteActive) && viewModel.ShowFloatingPill) _pillWindow.ShowWithoutActivation();
                else _pillWindow.HidePill();
            };
            desktop.MainWindow = window;
            desktop.Exit += async (_, _) =>
            {
                await AppServices.Get<IGlobalHotkeyService>().DisposeAsync();
                await AppServices.Get<Sessions.ApplicationOrchestrator>().DisposeAsync();
                await AppServices.Host.StopAsync();
                AppServices.Host.Dispose();
                Serilog.Log.CloseAndFlush();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OpenFromTray(object? sender, EventArgs args)
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitFromTray(object? sender, EventArgs args) => _mainWindow?.ExitApplication();
}
