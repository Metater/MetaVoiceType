using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MetaVoiceType.UI.Controls;

public sealed class SpectrumControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> BarsProperty =
        AvaloniaProperty.Register<SpectrumControl, IReadOnlyList<double>?>(nameof(Bars));
    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<SpectrumControl, IBrush?>(nameof(BarBrush));

    static SpectrumControl()
    {
        AffectsRender<SpectrumControl>(BarsProperty, BarBrushProperty);
    }

    public IReadOnlyList<double>? Bars { get => GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        IReadOnlyList<double>? bars = Bars;
        if (bars is null || bars.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        IBrush brush = BarBrush ?? Brushes.MediumPurple;
        double slot = Bounds.Width / bars.Count;
        double width = Math.Max(1, slot * 0.55);
        for (int index = 0; index < bars.Count; index++)
        {
            double height = Math.Max(2, Math.Clamp(bars[index], 0, 1) * Bounds.Height);
            double x = index * slot + (slot - width) / 2;
            context.FillRectangle(brush, new Rect(x, Bounds.Height - height, width, height), (float)Math.Min(2, width / 2));
        }
    }
}
