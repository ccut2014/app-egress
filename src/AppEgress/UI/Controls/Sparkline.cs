using System.Windows;
using System.Windows.Media;

namespace AppEgress.UI.Controls;

/// <summary>
/// Мини-график скорости за последнюю минуту: основная серия (приём) с заливкой и вторая (отдача) линией.
/// Рисуется в OnRender, без визуального дерева, так что даже десятки графиков обновлять дёшево.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    /// <summary>Минимальная шкала, 64 КБ/с: иначе фоновый шум растягивается на всю высоту.</summary>
    private const double MinScale = 64 * 1024;

    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PrimaryBrushProperty = DependencyProperty.Register(
        nameof(PrimaryBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryBrushProperty = DependencyProperty.Register(
        nameof(SecondaryBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Primary
    {
        get => (IReadOnlyList<double>?)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public IReadOnlyList<double>? Secondary
    {
        get => (IReadOnlyList<double>?)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public Brush PrimaryBrush
    {
        get => (Brush)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Brush SecondaryBrush
    {
        get => (Brush)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2)
            return;

        if (GridBrush != null)
        {
            var gridPen = new Pen(GridBrush, 1);
            dc.DrawLine(gridPen, new Point(0, h - 0.5), new Point(w, h - 0.5));
        }

        var primary = Primary;
        var secondary = Secondary;
        double max = MinScale;
        if (primary != null) foreach (var v in primary) max = Math.Max(max, v);
        if (secondary != null) foreach (var v in secondary) max = Math.Max(max, v);
        // Небольшой запас сверху, чтобы пик не упирался в край.
        max *= 1.12;

        if (secondary is { Count: > 1 })
            DrawSeries(dc, secondary, max, w, h, SecondaryBrush, fill: false);
        if (primary is { Count: > 1 })
            DrawSeries(dc, primary, max, w, h, PrimaryBrush, fill: true);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, Brush brush, bool fill)
    {
        double step = w / (values.Count - 1);
        Point P(int i) => new(i * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 3));

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i < values.Count; i++)
                    ctx.LineTo(P(i), true, true);
                ctx.LineTo(new Point(w, h), true, true);
            }
            area.Freeze();
            var color = brush is SolidColorBrush s ? s.Color : Colors.DodgerBlue;
            var gradient = new LinearGradientBrush(
                Color.FromArgb(0x55, color.R, color.G, color.B),
                Color.FromArgb(0x00, color.R, color.G, color.B),
                new Point(0, 0), new Point(0, 1));
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < values.Count; i++)
                ctx.LineTo(P(i), true, true);
        }
        line.Freeze();
        var pen = new Pen(brush, fill ? 1.6 : 1.2) { LineJoin = PenLineJoin.Round };
        if (!fill)
            pen.DashStyle = new DashStyle([2, 2], 0);
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
