using System.Windows;
using System.Windows.Media;

namespace DittoShell.App.Controls;

/// <summary>
/// Lucide 图标控件（轻量 FrameworkElement，非 Shape）。
/// 几何数据为 24×24 坐标系，按控件尺寸等比缩放绘制。
/// 用法：&lt;controls:Icon Kind="Search" Width="16" Height="16" Stroke="{StaticResource B.Text3}" /&gt;
/// </summary>
public sealed class Icon : FrameworkElement
{
    private const double ViewBox = 24.0;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private Geometry? _geometry;

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (Icon)d;
        self._geometry = e.NewValue is string key ? Resolve(key) : null;
        self.InvalidateVisual();
    }

    private static Geometry? Resolve(string key)
    {
        var resourceKey = "Icon." + key;
        var app = System.Windows.Application.Current;
        if (app is null) return null;

        if (app.TryFindResource(resourceKey) is Geometry g) return g;

        foreach (var dict in app.Resources.MergedDictionaries)
        {
            if (dict.Contains(resourceKey) && dict[resourceKey] is Geometry g2) return g2;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry is null)
        {
            // 诊断用：缺图标时画一个淡框，而不是静默消失
            var debugPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 0, 0)), 1);
            dc.DrawRectangle(null, debugPen, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double scale = Math.Min(w / ViewBox, h / ViewBox);
        double dx = (w - ViewBox * scale) / 2.0;
        double dy = (h - ViewBox * scale) / 2.0;

        var pen = new Pen(Stroke ?? Brushes.Black, Math.Max(0.5, StrokeThickness / Math.Max(scale, 0.01)))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, _geometry);
        dc.Pop();
        dc.Pop();
    }
}
