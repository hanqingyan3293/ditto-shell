using System.Text.RegularExpressions;

namespace DittoShell.Application.Services;

public sealed record SensitiveHit(string Kind, string Raw, int Start, int Length)
{
    public string Masked =>
        Raw.Length <= 12 ? new string('•', Raw.Length)
                         : $"{Raw[..6]}{new string('•', Math.Min(16, Raw.Length - 10))}{Raw[^4..]}";
}

/// <summary>
/// 本地敏感信息检测（不联网）。用于列表打码、AI 发送前拦截。
/// </summary>
public sealed class SensitiveDetector
{
    private sealed record Rule(string Kind, Regex Rx);

    private static readonly Rule[] Rules =
    {
        new("API Key", new Regex(@"sk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled)),
        new("GitHub 令牌", new Regex(@"github_pat_[A-Za-z0-9_]{40,}", RegexOptions.Compiled)),
        new("GitHub 令牌", new Regex(@"gh[pousr]_[A-Za-z0-9]{30,255}", RegexOptions.Compiled)),
        new("AWS 密钥", new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        new("私钥", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)),
        new("JWT", new Regex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)),
        new("数据库连接串", new Regex(@"(?i)(mongodb|mysql|postgres|postgresql|redis|amqp)://[^\s""']{6,}", RegexOptions.Compiled)),
        new("密码", new Regex(@"(?i)(password|passwd|pwd|secret|token|密码|口令)\s*[:=]\s*\S{4,}", RegexOptions.Compiled)),
        new("身份证号", new Regex(@"[1-9]\d{5}(18|19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{3}[\dXx]", RegexOptions.Compiled)),
        new("银行卡号", new Regex(@"\b\d{16,19}\b", RegexOptions.Compiled)),
    };

    /// <summary>检测文本里的敏感片段。银行卡会额外做 Luhn 校验，避免订单号误报。</summary>
    public IReadOnlyList<SensitiveHit> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<SensitiveHit>();

        var hits = new List<SensitiveHit>();

        foreach (var rule in Rules)
        {
            foreach (Match m in rule.Rx.Matches(text))
            {
                if (rule.Kind == "银行卡号" && !PassesLuhn(m.Value)) continue;
                hits.Add(new SensitiveHit(rule.Kind, m.Value, m.Index, m.Length));
            }
        }

        return hits.OrderBy(h => h.Start).ToList();
    }

    public bool HasSensitive(string? text) => Detect(text).Count > 0;

    /// <summary>把敏感片段替换成打码形式（保留位置，长度不变）。</summary>
    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var hits = Detect(text);
        if (hits.Count == 0) return text;

        var sb = new System.Text.StringBuilder(text);
        foreach (var h in hits)
        {
            var masked = h.Raw.Length <= 8
                ? new string('•', h.Raw.Length)
                : h.Raw[..4] + new string('•', Math.Min(12, h.Raw.Length - 8)) + h.Raw[^4..];

            // 长度对齐，避免打乱后续匹配位置
            if (masked.Length != h.Raw.Length)
                masked = masked.PadRight(h.Raw.Length, '•').Substring(0, h.Raw.Length);

            sb.Remove(h.Start, h.Length).Insert(h.Start, masked);
        }
        return sb.ToString();
    }

    private static bool PassesLuhn(string digits)
    {
        int sum = 0;
        bool alt = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i])) return false;
            int n = digits[i] - '0';

            if (alt)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }

            sum += n;
            alt = !alt;
        }
        return sum % 10 == 0;
    }
}
