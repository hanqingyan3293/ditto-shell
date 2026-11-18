using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace DittoShell.App.Services;

/// <summary>
/// AI 会话存活守卫：
/// 记录正在跑的 AI 任务，切页/失焦/最小化时不受影响。
/// 发生异常终止时留下诊断日志。
/// </summary>
public static class AiSessionGuard
{
    private static int _runningCount;
    private static string _lastPhase = "";
    private static DateTime _startedAt;
    private static readonly List<string> _log = new();

    public static bool IsRunning => _runningCount > 0;
    public static string LastPhase => _lastPhase;

    /// <summary>标记会话开始。</summary>
    public static IDisposable Begin(string label)
    {
        Interlocked.Increment(ref _runningCount);
        _lastPhase = label;
        _startedAt = DateTime.Now;
        Append($"BEGIN {label}");
        return new Scope(label);
    }

    /// <summary>更新阶段。</summary>
    public static void Phase(string phase)
    {
        _lastPhase = phase;
        Append($"PHASE {phase}");
    }

    /// <summary>记录异常终止。</summary>
    public static void Fail(string reason, Exception? ex = null)
    {
        Append($"FAIL {reason}" + (ex is null ? "" : $": {ex.GetType().Name} {ex.Message}"));
    }

    /// <summary>记录窗口状态变化（用于排查「离开窗口就停」）。</summary>
    public static void WindowState(string state)
    {
        if (_runningCount > 0)
            Append($"WINDOW {state} (session still running, phase={_lastPhase})");
    }

    /// <summary>读最近的日志（诊断用）。</summary>
    public static string Dump()
        => string.Join(Environment.NewLine, _log.TakeLast(60));

    private static void Append(string line)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (_log.Count > 200) _log.RemoveRange(0, 100);
        }

        // 同时写文件（用户可发给我）
        try
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "ai-session.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _label;
        private bool _disposed;

        public Scope(string label) => _label = label;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Interlocked.Decrement(ref _runningCount);
            var elapsed = (DateTime.Now - _startedAt).TotalSeconds;
            Append($"END {_label} ({elapsed:F1}s)");
        }
    }
}
