using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DittoShell.App.Views;

/// <summary>
/// 原图查看器。
/// 缩放：以鼠标所在点为中心，无上限（下限 5%）。
/// 平移：左键拖动。
/// 复位：双击或按 0。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly BitmapSource _bitmap;
    private bool _dragging;
    private Point _lastPos;
    private double _scale = 1.0;
    private Point _translate;

    public ImageViewerWindow(BitmapSource bitmap, string? title = null)
    {
        InitializeComponent();
        _bitmap = bitmap;

        Img.Source = bitmap;
        TitleText.Text = title ?? $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";

        Loaded += (_, _) => Reset();
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.D0 or Key.NumPad0: Reset(); break;
                case Key.D1 or Key.NumPad1: ActualSize(); break;
            }
        };
    }

    // ───────────── 视图 ─────────────

    /// <summary>回到「适应窗口」：Scale=1 即 Image Stretch=Uniform 的默认尺寸。</summary>
    private void Reset()
    {
        _scale = 1;
        _translate = new Point(0, 0);
        Apply();
    }

    /// <summary>原始像素 1:1：把自适应后的比例换算回 100%。</summary>
    private void ActualSize()
    {
        if (Host.ActualWidth <= 0 || _bitmap.PixelWidth == 0) return;

        double auto = Math.Min(
            (Host.ActualWidth - 20) / _bitmap.PixelWidth,
            (Host.ActualHeight - 20) / _bitmap.PixelHeight);

        if (auto <= 0) return;

        _scale = 1 / auto;
        _translate = new Point(0, 0);
        Apply();
    }

    private void Apply()
    {
        ScaleT.ScaleX = _scale;
        ScaleT.ScaleY = _scale;
        TransT.X = _translate.X;
        TransT.Y = _translate.Y;

        double auto = Host.ActualWidth > 0
            ? Math.Min((Host.ActualWidth - 20) / _bitmap.PixelWidth,
                       (Host.ActualHeight - 20) / _bitmap.PixelHeight)
            : 1;

        double realPercent = _scale * auto * 100;
        ZoomText.Text = $"{realPercent:F0}%   ·   {_bitmap.PixelWidth} × {_bitmap.PixelHeight}   ·   滚轮放大看细节";
    }

    // ───────────── 交互 ─────────────

    private void Host_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;

        // 以鼠标点为中心缩放：换算平移量，使该点在屏幕上保持不动
        var p = e.GetPosition(Host);
        var c = new Point(Host.ActualWidth / 2, Host.ActualHeight / 2);

        _translate = new Point(
            (p.X - c.X) * (1 - factor) + _translate.X * factor,
            (p.Y - c.Y) * (1 - factor) + _translate.Y * factor);

        _scale = Math.Max(0.05, _scale * factor);
        Apply();

        e.Handled = true;
    }

    private void Host_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Reset();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastPos = e.GetPosition(Host);
        Host.CaptureMouse();
        e.Handled = true;
    }

    private void Host_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Host.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var p = e.GetPosition(Host);
        var d = p - _lastPos;
        _lastPos = p;

        _translate = new Point(_translate.X + d.X, _translate.Y + d.Y);
        Apply();
    }

    // ───────────── 工具条 ─────────────

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
            DragMove();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Reset();

    private void Actual_Click(object sender, RoutedEventArgs e) => ActualSize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
