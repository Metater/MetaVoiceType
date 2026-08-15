using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MetaVoiceType.UI.ViewModels;
using MetaVoiceType.UI.Views;
using System.Runtime.CompilerServices;

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

    [AvaloniaFact]
    public void MandatoryOnboardingContinueButtonAdvancesTheViewModel()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        viewModel.ShowOnboarding = true;
        viewModel.OnboardingStep = 1;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        Button button = window.FindControl<Button>("OnboardingContinueButton")!;
        Assert.NotNull(button);
        Assert.True(button.IsVisible);
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
        button.Command.Execute(null);
        Assert.Equal(2, viewModel.OnboardingStep);

        window.ExitApplication();
    }
}
