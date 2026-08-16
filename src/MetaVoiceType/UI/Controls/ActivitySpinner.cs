using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MetaVoiceType.UI.Controls;

public sealed class ActivitySpinner : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<ActivitySpinner, bool>(nameof(IsActive));
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<ActivitySpinner, IBrush?>(nameof(Brush));
    private readonly DispatcherTimer _timer;
    private int _phase;

    public ActivitySpinner()
    {
        _timer = new(TimeSpan.FromMilliseconds(90), DispatcherPriority.Render, (_, _) => { _phase = (_phase + 1) % 8; InvalidateVisual(); });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            if (IsActive) _timer.Start(); else _timer.Stop();
            InvalidateVisual();
        }
    }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (!IsActive) return;
        IBrush brush = Brush ?? Brushes.MediumPurple;
        Point center = new(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(2, Math.Min(Bounds.Width, Bounds.Height) / 2 - 2);
        for (int index = 0; index < 8; index++)
        {
            double angle = index * Math.PI / 4;
            double opacity = 0.2 + 0.8 * ((index - _phase + 8) % 8) / 7d;
            using (context.PushOpacity(opacity))
                context.DrawEllipse(brush, null, new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius), 1.6, 1.6);
        }
    }
}
