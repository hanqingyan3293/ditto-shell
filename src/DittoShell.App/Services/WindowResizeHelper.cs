using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DittoShell.App.Services;

/// <summary>
/// 无边框窗口（WindowStyle=None）的边角拖拽缩放。
/// 全部用物理像素比较，避免 DPI 缩放导致判定区域偏移。
/// </summary>
public static class WindowResizeHelper
{
    private const int WM_NCHITTEST = 0x0084;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    /// <summary>边缘判定宽度（DIP，会自动按 DPI 放大）。</summary>
    public static void Enable(Window window, double border = 8)
    {
        if (window.IsLoaded) { Attach(window, border); return; }
        window.SourceInitialized += (_, _) => Attach(window, border);
    }

    private static void Attach(Window window, double borderDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null) return;

        // DPI 缩放：DIP -> 物理像素
        double scale = 1.0;
        try
        {
            if (source.CompositionTarget is not null)
                scale = source.CompositionTarget.TransformToDevice.M11;
        }
        catch { }

        int border = Math.Max(6, (int)Math.Round(borderDip * scale));

        source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            if (handled) return IntPtr.Zero;

            if (window.WindowState == WindowState.Maximized) return IntPtr.Zero;
            if (!window.IsVisible) return IntPtr.Zero;

            if (!GetCursorPos(out var pt)) return IntPtr.Zero;
            if (!GetWindowRect(h, out var rc)) return IntPtr.Zero;

            bool left = pt.X <= rc.Left + border && pt.X >= rc.Left;
            bool right = pt.X >= rc.Right - border && pt.X <= rc.Right;
            bool top = pt.Y <= rc.Top + border && pt.Y >= rc.Top;
            bool bottom = pt.Y >= rc.Bottom - border && pt.Y <= rc.Bottom;

            if (!left && !right && !top && !bottom) return IntPtr.Zero;

            int hit;
            if (left && top) hit = HTTOPLEFT;
            else if (right && top) hit = HTTOPRIGHT;
            else if (left && bottom) hit = HTBOTTOMLEFT;
            else if (right && bottom) hit = HTBOTTOMRIGHT;
            else if (left) hit = HTLEFT;
            else if (right) hit = HTRIGHT;
            else if (top) hit = HTTOP;
            else hit = HTBOTTOM;

            handled = true;
            return new IntPtr(hit);
        });
    }
}
