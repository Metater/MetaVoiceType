using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MetaVoiceType.UI.Views;

public sealed partial class PillWindow : Window
{
    public PillWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PositionAtBottomCenter();
    }

    public void ShowWithoutActivation()
    {
        PositionAtBottomCenter();
        if (!IsVisible) Show();
    }

    private void PositionAtBottomCenter()
    {
        Screen? screen = Screens.Primary;
        if (screen is null) return;
        PixelRect area = screen.WorkingArea;
        Position = new PixelPoint(area.X + (area.Width - (int)Width) / 2, area.Bottom - (int)Height - 22);
    }
}
