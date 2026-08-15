using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MetaVoiceType.UI.Views;

public sealed partial class TrayNoticeWindow : Window
{
    public TrayNoticeWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            Screen? screen = Screens.Primary;
            if (screen is not null)
            {
                PixelRect area = screen.WorkingArea;
                Position = new(area.Right - (int)Width - 20, area.Bottom - (int)Height - 20);
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        };
    }
}
