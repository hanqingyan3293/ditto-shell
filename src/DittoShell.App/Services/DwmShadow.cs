using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DittoShell.App.Services;

/// <summary>
/// 给无边框窗口加系统级阴影（Windows 资源管理器那种）。
/// WPF 的 WindowChrome 在 GlassFrameThickness=0 时不给阴影，
/// 需要直接告诉 DWM「这个窗口有非客户区」。
/// </summary>
public static class DwmShadow
{
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_ENABLED = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>在窗口 SourceInitialized 之后调用。</summary>
    public static void Enable(Window window, int margin = 1)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var m = new MARGINS
            {
                Left = margin,
                Right = margin,
                Top = margin,
                Bottom = margin
            };
            DwmExtendFrameIntoClientArea(hwnd, ref m);

            int policy = DWMNCRP_ENABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        }
        catch
        {
            // DWM 不可用（极老的系统 / 远程桌面）时静默跳过，窗口仍可用
        }
    }
}
