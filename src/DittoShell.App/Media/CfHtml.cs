using System.Text;

namespace DittoShell.App.Media;

/// <summary>
/// 解析 Windows 剪贴板的 "HTML Format"（CF_HTML）。
/// 它的格式是：一段 Description 头 + HTML 正文，
/// 正文起点由头里的 StartHTML 字段给出。
/// </summary>
public static class CfHtml
{
    public static string? Extract(byte[]? raw)
    {
        if (raw is null || raw.Length < 20) return null;

        string text;
        try { text = Encoding.UTF8.GetString(raw); }
        catch { return null; }

        // 找 StartHTML
        int start = FindOffset(text, "StartHTML:");
        if (start >= 0 && start < text.Length)
        {
            var html = text[start..];
            // 补一点基础样式，让白底黑字更易读
            return Wrap(html);
        }

        // 没有头部就当作纯 HTML
        var idx = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? Wrap(text[idx..]) : null;
    }

    private static int FindOffset(string text, string key)
    {
        var i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;

        var p = i + key.Length;
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;

        var sb = new StringBuilder();
        while (p < text.Length && char.IsDigit(text[p])) sb.Append(text[p++]);

        return int.TryParse(sb.ToString(), out var n) && n > 0 && n < text.Length ? n : -1;
    }

    private static string Wrap(string html) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<style>body{font-family:'Segoe UI','Microsoft YaHei UI',sans-serif;font-size:13px;" +
        "color:#1f2328;margin:8px;line-height:1.6;word-wrap:break-word}" +
        "img{max-width:100%;height:auto}pre{white-space:pre-wrap}" +
        "a{color:#1677ff}</style></head><body>" + html + "</body></html>";
}
