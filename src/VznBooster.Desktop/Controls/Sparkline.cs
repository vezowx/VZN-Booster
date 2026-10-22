using System.Windows;
using System.Windows.Media;

namespace VznBooster.Desktop.Controls;

/// <summary>Gráfico de linha leve, desenhado direto no DrawingContext (sem bibliotecas). Sem dados suficientes, não desenha a linha.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1);
        gridPen.Freeze();
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var values = Values;
        if (values is null || values.Count < 2) return;

        var max = Maximum > 0 ? Maximum : 100.0;
        var capacity = Math.Max(2, Capacity);
        var n = Math.Min(values.Count, capacity);
        var offset = values.Count - n;
        var step = w / (capacity - 1);

        var points = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var x = w - (n - 1 - i) * step;
            var ratio = Math.Clamp(values[offset + i] / max, 0, 1);
            points[i] = new Point(x, h - 2 - ratio * (h - 4));
        }

        if (Stroke is SolidColorBrush sb)
        {
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h), true, true);
                foreach (var p in points) ctx.LineTo(p, true, false);
                ctx.LineTo(new Point(points[n - 1].X, h), true, false);
            }
            fill.Freeze();
            var c = sb.Color;
            var gradient = new LinearGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, fill);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < n; i++) ctx.LineTo(points[i], true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, line);
    }
}
