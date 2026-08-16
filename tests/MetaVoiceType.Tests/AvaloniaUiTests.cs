using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MetaVoiceType.UI.ViewModels;
using MetaVoiceType.UI.Views;
using MetaVoiceType.Sessions;
using MetaVoiceType.VoiceCommands;
using System.Reflection;
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

    [AvaloniaFact]
    public void VoskOnboardingContinueEnablesOnlyAfterSelectedLanguageIsActive()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var orchestrator = (ApplicationOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(ApplicationOrchestrator));
        SetField(viewModel, "_orchestrator", orchestrator);
        SetGeneratedField(viewModel, "selectedVoiceLanguage", VoiceCommandCatalog.LoadBundled().Get("en-us"));
        viewModel.ShowOnboarding = true;
        viewModel.OnboardingStep = 3;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        Button button = window.FindControl<Button>("OnboardingContinueButton")!;
        Assert.False(button.IsEnabled);
        button.Command!.Execute(null);
        Assert.Equal(3, viewModel.OnboardingStep);

        SetField(orchestrator, "_activeVoiceLanguageId", "en-us");
        viewModel.OnboardingStep = 2;
        viewModel.OnboardingStep = 3;
        Assert.True(button.IsEnabled);
        button.Command.Execute(null);
        Assert.Equal(4, viewModel.OnboardingStep);

        window.ExitApplication();
    }

    private static void SetField(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetGeneratedField(object target, string nameFragment, object? value)
    {
        FieldInfo field = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(x => x.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
        field.SetValue(target, value);
    }
}
