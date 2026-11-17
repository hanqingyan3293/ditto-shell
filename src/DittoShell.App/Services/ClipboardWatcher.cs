using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DittoShell.App.Services;

public sealed record ClipboardSnapshot(string? Text, string? ProcessName, string? WindowTitle, int TextLength);

/// <summary>
/// 轻量剪贴板监听：只用来记录「这条内容来自哪个程序」。
/// 不接管捕获（捕获仍由 Ditto 负责），也不修改剪贴板。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private HwndSource? _source;
    private System.Windows.Threading.DispatcherTimer? _debounce;

    /// <summary>剪贴板变化（已延迟到 Ditto 写完数据库之后）。</summary>
    public event Action<ClipboardSnapshot>? Captured;

    /// <summary>延迟多久再取（给 Ditto 的处理线程留时间），默认 900ms。</summary>
    public int DelayMs { get; set; } = 900;

    public bool Start()
    {
        if (_source is not null) return true;

        try
        {
            var p = new HwndSourceParameters("DittoShellClipboardSink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);

            return AddClipboardFormatListener(_source.Handle);
        }
        catch
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // 去抖：连续复制只处理最后一次
            _debounce?.Stop();

            _debounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DelayMs)
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce?.Stop();
                _ = CaptureAsync();
            };
            _debounce.Start();

            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task CaptureAsync()
    {
        try
        {
            string? text = null;

            // 剪贴板可能被别的程序占着，重试几次
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        text = System.Windows.Clipboard.GetText();
                        break;
                    }
                    // 非文本内容（图片/文件）也记一条，只是没有文本
                    return;
                }
                catch
                {
                    await Task.Delay(80);
                }
            }

            if (string.IsNullOrEmpty(text)) return;

            var (proc, title) = ForegroundInfo();
            Captured?.Invoke(new ClipboardSnapshot(text, proc, title, text.Length));
        }
        catch
        {
            // 监听失败不影响主流程
        }
    }

    private static (string? Process, string? Title) ForegroundInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (null, null);

            GetWindowThreadProcessId(hwnd, out uint pid);
            string? proc = null;

            try
            {
                using var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { }

            int len = GetWindowTextLength(hwnd);
            string? title = null;
            if (len > 0)
            {
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
                if (title.Length > 200) title = title[..200];
            }

            return (proc, title);
        }
        catch
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        _debounce?.Stop();
        _debounce = null;

        if (_source is null) return;
        try
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch { }
        _source = null;
    }
}
