using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MetaVoiceType.UI.Views;

namespace MetaVoiceType.Tests;

public sealed class AvaloniaUiTests
{
    [AvaloniaFact]
    public void MainWindowXamlLoadsAndLaysOut()
    {
        var window = new MainWindow();

        window.Show();

        Assert.NotNull(window.Content);
        Assert.IsType<Grid>(window.Content);
        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);

        window.ExitApplication();
    }
}
