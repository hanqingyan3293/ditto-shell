using System.Runtime.InteropServices;

namespace DittoShell.Infrastructure.Ipc;

/// <summary>
/// 与运行中的 Ditto 进程通信：命令行粘贴、窗口消息刷新/备份。
/// 不做键鼠模拟、不注入、不 hook。
/// </summary>
public sealed class DittoBridge : DittoShell.Domain.Abstractions.IDittoBridge
{
    // 消息号来自 reference/ditto-src/src/Misc.h（WM_USER = 0x0400）
    private const uint WM_REFRESH_VIEW = 0x0400 + 205;   // 1229
    private const uint WM_BACKUP_DB = 0x0400 + 231;      // 1255
    private const uint WM_CLOSE_APP = 0x0400 + 228;      // 1228 附近，实际用 /close 更稳
    private const uint WM_PASTE_CLIP = 0x0400 + 233;     // 1257

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly string _exePath;

    public DittoBridge(string exePath) => _exePath = exePath;

    /// <summary>快速粘贴窗口的类名固定为 "Ditto"（MainFrm.cpp 注册）。</summary>
    private static IntPtr MainWindow()
    {
        var hwnd = FindWindow("Ditto", null);
        return hwnd;
    }

    public Task<bool> IsRunningAsync(CancellationToken ct = default)
        => Task.FromResult(MainWindow() != IntPtr.Zero);

    /// <summary>
    /// 粘贴：让 Ditto 自己完成格式还原 + Ctrl+V。
    /// 命令行 /paste:ID 由第二个实例转交给运行中的实例后立即退出。
    /// </summary>
    public Task PasteAsync(long clipId, bool plainText, CancellationToken ct = default)
    {
        var args = plainText ? $"/paste:{clipId} /PlainTextPaste" : $"/paste:{clipId}";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_exePath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法调用 Ditto 粘贴：{ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>通知 Ditto 重新从数据库加载列表（写入后必须调用）。</summary>
    public Task RefreshViewAsync(CancellationToken ct = default)
    {
        var hwnd = MainWindow();
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_REFRESH_VIEW, IntPtr.Zero, IntPtr.Zero);

        return Task.CompletedTask;
    }

    /// <summary>让 Ditto 自己备份数据库（会弹出它自己的提示框，故默认不用）。</summary>
    public Task BackupDatabaseAsync(CancellationToken ct = default)
    {
        var hwnd = MainWindow();
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_BACKUP_DB, IntPtr.Zero, IntPtr.Zero);

        return Task.CompletedTask;
    }

    /// <summary>关闭 Ditto 的原界面（外壳接管后隐藏它）。</summary>
    public Task CloseOriginalWindowAsync(CancellationToken ct = default)
    {
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero) return Task.CompletedTask;

        // 用命令行 /close 走官方路径，避免直接发消息导致状态不一致
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_exePath, "/close")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }

        return Task.CompletedTask;
    }

    /// <summary>直接给窗口发粘贴消息（命令行开销过大时的备选，暂不使用）。</summary>
    public Task PasteViaMessageAsync(long clipId, CancellationToken ct = default)
    {
        var hwnd = MainWindow();
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_PASTE_CLIP, new IntPtr(clipId), IntPtr.Zero);

        return Task.CompletedTask;
    }
}
