using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>
/// 把「剪贴板变化」对齐到 Ditto 的条目，并记录来源应用。
/// 对齐方式：用文本前 N 字符在 Main.mText 里做前缀匹配，取最新一条。
/// </summary>
public sealed class SourceTrackingService
{
    private const int MatchPrefixLength = 120;

    private readonly IClipReader _reader;
    private readonly ISourceStore _sidecar;

    public SourceTrackingService(IClipReader reader, ISourceStore sidecar)
    {
        _reader = reader;
        _sidecar = sidecar;
    }

    /// <summary>处理一次剪贴板快照。返回记录到的 clipId（0 表示没对齐上）。</summary>
    public async Task<long> HandleSnapshotAsync(string? text, string? process, string? title,
                                                CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || _sidecar is null) return 0;

        try
        {
            var prefix = text.Length > MatchPrefixLength ? text[..MatchPrefixLength] : text;
            var prefixNoNewline = prefix.Replace("\r", " ").Replace("\n", " ");

            var items = await _reader.SearchAsync(prefix, null, null, null, null, ct);
            var hit = items.FirstOrDefault();

            // 前缀没命中就退一步用前 20 字符
            if (hit is null && prefixNoNewline.Length > 20)
            {
                items = await _reader.SearchAsync(prefixNoNewline[..20], null, null, null, null, ct);
                hit = items.FirstOrDefault();
            }

            if (hit is null)
            {
                // 用前 12 个字符兜底
                var shortKey = prefixNoNewline.Length > 12 ? prefixNoNewline[..12] : prefixNoNewline;
                if (shortKey.Length >= 4)
                {
                    items = await _reader.SearchAsync(shortKey, null, null, null, null, ct);
                    hit = items.FirstOrDefault();
                }
            }

            if (hit is null) return 0;

            await _sidecar.RecordSourceAsync(hit.Id, process, title, hit.Crc, ct);
            return hit.Id;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>取某条目的来源（供 UI 显示）。</summary>
    public async Task<string?> DescribeSourceAsync(long clipId, CancellationToken ct = default)
    {
        var src = await _sidecar.GetSourceAsync(clipId, ct);
        if (src is null) return null;

        var (app, title) = src.Value;
        if (string.IsNullOrEmpty(app)) return null;

        // chrome.exe -> Chrome
        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        name = char.ToUpperInvariant(name[0]) + name[1..];

        return string.IsNullOrEmpty(title) ? name : $"{name} · {Truncate(title, 30)}";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
