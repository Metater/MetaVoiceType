using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MetaVoiceType.UI.Views;

public sealed partial class PillWindow : Window
{
    private IDisposable? _spectrumLease;
    public PillWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PositionAtBottomCenter();
        PillSurface.AddHandler(PointerPressedEvent, PillPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public event EventHandler? OpenMainWindowRequested;

    private void PillPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowWithoutActivation()
    {
        PositionAtBottomCenter();
        if (!IsVisible) { _spectrumLease ??= AppServices.TryGet<Audio.AudioSpectrumService>()?.Acquire(); Show(); }
    }

    public void HidePill() { Hide(); _spectrumLease?.Dispose(); _spectrumLease = null; }

    private void PositionAtBottomCenter()
    {
        Screen? screen = Screens.Primary;
        if (screen is null) return;
        PixelRect area = screen.WorkingArea;
        Position = new PixelPoint(area.X + (area.Width - (int)Width) / 2, area.Bottom - (int)Height - 22);
    }
}
