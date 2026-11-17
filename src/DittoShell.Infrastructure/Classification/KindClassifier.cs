using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using DittoShell.Domain.Models;

namespace DittoShell.Infrastructure.Classification;

/// <summary>
/// 本地启发式类型判定。顺序很重要：凭据 → 链接 → 路径 → 代码 → 文本。
/// 目标不是 100% 准确，而是让列表上的图标和色点看起来合理。
/// </summary>
public static class KindClassifier
{
    private static readonly Regex LinkRx = new(
        @"^\s*(https?|ftp|file|mailto):\/\/?\S+$|^\s*www\.\S+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PathRx = new(
        @"^\s*([a-zA-Z]:\\|\\\\|\.{0,2}\/)[^\r\n]{1,400}$",
        RegexOptions.Compiled);

    private static readonly Regex[] SensitiveRx =
    {
        new(@"sk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
        new(@"gh[pousr]_[A-Za-z0-9]{30,}", RegexOptions.Compiled),
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled),
        new(@"(?i)(password|passwd|pwd|密码)\s*[:=]\s*\S{4,}", RegexOptions.Compiled),
        new(@"(?i)(mongodb|mysql|postgres|redis|amqp)://[^\s""']+", RegexOptions.Compiled),
        new(@"[1-9]\d{5}(18|19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{3}[\dXx]", RegexOptions.Compiled),
    };

    /// <summary>命令：以常见 CLI 开头，且是短文本、不含中文句读。</summary>
    private static readonly Regex CommandRx = new(
        @"^\s*(git|npm|pnpm|yarn|docker|kubectl|helm|sudo|systemctl|brew|apt|apt-get|pip|pip3|dotnet|nuget|mvn|gradle|cargo|go|rustc|node|python|python3|pwsh|powershell|cmd|ssh|scp|rsync|curl|wget|tar|zip|7z|chmod|chown|mkdir|rm|cp|mv|ls|cd|cat|grep|find|sed|awk)\s+\S",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>邮箱。</summary>
    private static readonly Regex EmailRx = new(
        @"^[\w.+-]+@[\w-]+\.[\w.-]{2,}$", RegexOptions.Compiled);

    /// <summary>手机号（中国大陆 11 位）。</summary>
    private static readonly Regex PhoneRx = new(
        @"^1[3-9]\d{9}$", RegexOptions.Compiled);

    private static readonly string[] CodeHints =
    {
        "using ", "import ", "function ", "def ", "class ", "public ", "private ",
        "SELECT ", "INSERT ", "UPDATE ", "DELETE ", "git ", "npm ", "dotnet ",
        "pip ", "docker ", "kubectl ", "sudo ", "cd ", "ls ", "Get-", "Set-",
        "#!/", "<?xml", "<!DOCTYPE", "console.log", "print(", "return ", "const ",
        "let ", "var ", "async ", "await ", "package ", "namespace "
    };

    public static ClipKind Classify(string text, bool isImage)
    {
        if (isImage) return ClipKind.Image;
        if (string.IsNullOrWhiteSpace(text)) return ClipKind.Unknown;

        var t = text.Trim();

        // 1) 凭据优先（避免被当成链接）
        foreach (var rx in SensitiveRx)
            if (rx.IsMatch(t)) return ClipKind.Secret;

        // 2) 联系方式（特征强，优先于链接）
        if (t.Length < 60 && !t.Contains((char)10))
        {
            if (EmailRx.IsMatch(t) || PhoneRx.IsMatch(t)) return ClipKind.Contact;
        }

        // 3) 命令（短 + CLI 开头 + 无中文句读）
        if (t.Length < 200 && !t.Contains((char)10)
            && !t.Contains('。') && !t.Contains('，') && !t.Contains('？') && !t.Contains('！'))
        {
            if (CommandRx.IsMatch(t)) return ClipKind.Command;
        }

        // 4) 链接
        // 4) 链接
        if (LinkRx.IsMatch(t) && !t.Contains('\n')) return ClipKind.Link;

        // 5) 文件 / 目录路径
        if (PathRx.IsMatch(t) && !t.Contains('\n')) return ClipKind.File;

        // 6) 代码打分
        if (ScoreCode(t) >= 2) return ClipKind.Code;

        // 7) 长文本
        if (t.Length > 500) return ClipKind.LongText;

        return ClipKind.Text;
    }

    private static int ScoreCode(string t)
    {
        int score = 0;

        foreach (var hint in CodeHints)
            if (t.StartsWith(hint, StringComparison.OrdinalIgnoreCase) ||
                t.Contains("\n" + hint, StringComparison.OrdinalIgnoreCase))
            { score += 2; break; }

        if (t.Contains("=>") || t.Contains("->")) score++;
        if (t.Contains('{') && t.Contains('}')) score++;
        if (t.Count(c => c == ';') >= 2) score++;
        if (t.Contains("</") && t.Contains('>')) score++;
        if (t.StartsWith("#") && t.Contains('\n')) score++;

        // 纯中文长文本不应被判成代码
        int cjk = t.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        if (cjk > t.Length * 0.35) score -= 2;

        return score;
    }
}
