using System.Windows;
using System.Windows.Media;

namespace ZeiLand.Controls;

/// <summary>
/// A battery as a ring: a faint track, and the level clockwise from the top. Drawn directly in OnRender (no child
/// elements); the device's icon goes in the middle, on top of it.
/// </summary>
internal sealed class BatteryRing : FrameworkElement
{
    private const double Thickness = 2.2;
    private static readonly Pen TrackPen = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)).Frozen(), Thickness).Frozen();

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 to 1; 0 (only the track) when the level isn't known.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = (size - Thickness) / 2;
        dc.DrawEllipse(null, TrackPen, center, radius, radius);

        double level = Math.Clamp(Level, 0, 1);
        if (level < 0.01) return;
        var pen = new Pen(Brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (level > 0.995)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        double angle = level * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius) };
        figure.Segments.Add(new ArcSegment(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
            new Size(radius, radius), 0, level > 0.5, SweepDirection.Clockwise, isStroked: true));
        var arc = new PathGeometry([figure]);
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
