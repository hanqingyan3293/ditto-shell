using System.IO;
using System.Text;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Infrastructure.Settings;

/// <summary>
/// Ditto.Settings 的读写。文件是 UTF-16LE 编码的 INI 文本（实测 142 行）。
/// 写入按行编辑：只改目标键，其余行（含未知键）原样保留。
/// </summary>
public sealed class DittoSettingsGateway : ISettingsGateway
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DittoSettingsGateway(string settingsPath) => _path = settingsPath;

    public string Path => _path;
    public bool Exists => File.Exists(_path);

    public async Task<IReadOnlyDictionary<string, string>> ReadAllAsync(CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return dict;

        await _gate.WaitAsync(ct);
        try
        {
            var text = await File.ReadAllTextAsync(_path, Encoding.Unicode, ct);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('[') || line.StartsWith(';')) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var val = line[(eq + 1)..];
                if (key.Length > 0) dict[key] = val;
            }
        }
        finally
        {
            _gate.Release();
        }

        return dict;
    }

    /// <summary>
    /// 按行编辑写入。changes 里有的键会被替换/追加，其它行保持不变。
    /// </summary>
    public async Task WriteAsync(IReadOnlyDictionary<string, string> changes,
                                 CancellationToken ct = default)
    {
        if (changes.Count == 0) return;
        if (!File.Exists(_path)) throw new FileNotFoundException("找不到 Ditto.Settings", _path);

        await _gate.WaitAsync(ct);
        try
        {
            var original = await File.ReadAllTextAsync(_path, Encoding.Unicode, ct);
            var newline = original.Contains("\r\n") ? "\r\n" : "\n";
            var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line.StartsWith('[')) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                if (!changes.TryGetValue(key, out var newVal)) continue;

                lines[i] = $"{key}={newVal}";
                handled.Add(key);
            }

            // 新键追加到 [Ditto] 段末尾（即文件末尾）
            var appended = changes.Where(kv => !handled.Contains(kv.Key)).ToList();
            if (appended.Count > 0)
            {
                while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                foreach (var kv in appended) lines.Add($"{kv.Key}={kv.Value}");
                lines.Add("");
            }

            var output = string.Join(newline, lines);
            await File.WriteAllTextAsync(_path, output, Encoding.Unicode, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取呼出快捷键（704 = Ctrl+`）。</summary>
    public async Task<long?> GetHotkeyAsync(CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        if (all.TryGetValue("DittoHotKey", out var v) && long.TryParse(v, out var n)) return n;
        return null;
    }

    /// <summary>把 Ditto 的热键整数值翻译成 "Ctrl+`" 这样的可读形式。</summary>
    public static string DescribeHotkey(long code)
    {
        if (code == 0) return "（未设置）";

        uint v = (uint)code;
        uint mods = (v >> 8) & 0xFF;
        uint vk = v & 0xFF;

        var parts = new List<string>();
        if ((mods & 0x1) != 0) parts.Add("Alt");
        if ((mods & 0x2) != 0) parts.Add("Ctrl");
        if ((mods & 0x4) != 0) parts.Add("Shift");
        if ((mods & 0x8) != 0) parts.Add("Win");

        parts.Add(vk switch
        {
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            _ => $"0x{vk:X2}"
        });

        return string.Join("+", parts);
    }
}
