using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DittoShell.App.Services;

/// <summary>
/// 全局热键。用一个隐藏消息窗口接收 WM_HOTKEY，不依赖主窗口生命周期。
/// 支持形如 "Ctrl+`" / "Ctrl+Shift+`" / "Alt+F1" 的写法。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    public const int IdQuickPanel = 0xD101;
    public const int IdMainWindow = 0xD102;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private HwndSource? _source;
    private readonly List<int> _registered = new();

    public event Action? QuickPanelPressed;
    public event Action? MainWindowPressed;

    /// <summary>上一次注册失败的原因（用于界面提示）。</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_source is not null) return;

        var p = new HwndSourceParameters("DittoShellHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool RegisterQuickPanel(string gesture) => Register(IdQuickPanel, gesture);

    public bool RegisterMainWindow(string gesture) => Register(IdMainWindow, gesture);

    public bool Register(int id, string gesture)
    {
        if (_source is null) Start();
        if (_source is null) return false;

        // 空字符串 = 用户主动不要这个快捷键
        if (string.IsNullOrWhiteSpace(gesture))
        {
            if (_source is not null) UnregisterHotKey(_source.Handle, id);
            _registered.Remove(id);
            LastError = null;
            return true;
        }

        if (!TryParse(gesture, out uint mods, out uint vk))
        {
            LastError = $"无法识别的快捷键：{gesture}";
            return false;
        }

        UnregisterHotKey(_source.Handle, id);
        bool ok = RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk);
        if (!ok)
        {
            LastError = $"快捷键 {gesture} 注册失败（可能已被其它程序占用）";
            return false;
        }

        if (!_registered.Contains(id)) _registered.Add(id);
        LastError = null;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == IdQuickPanel) { QuickPanelPressed?.Invoke(); handled = true; }
            else if (id == IdMainWindow) { MainWindowPressed?.Invoke(); handled = true; }
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].ToLowerInvariant();
            if (i < parts.Length - 1)
            {
                switch (p)
                {
                    case "ctrl" or "control": mods |= MOD_CONTROL; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "win" or "meta": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            else
            {
                vk = ToVirtualKey(parts[i]);
                if (vk == 0) return false;
            }
        }
        return true;
    }

    private static uint ToVirtualKey(string key)
    {
        var k = key.Trim();
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c >= 'A' && c <= 'Z') return c;                    // 0x41-0x5A
            if (c >= '0' && c <= '9') return c;                    // 0x30-0x39
            return c switch
            {
                '`' => 0xC0,   // VK_OEM_3
                '~' => 0xC0,
                '-' => 0xBD,
                '=' => 0xBB,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                ';' => 0xBA,
                '\'' => 0xDE,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                _ => 0
            };
        }

        return k.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => 0
        };
    }

    public void Dispose()
    {
        if (_source is null) return;
        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
