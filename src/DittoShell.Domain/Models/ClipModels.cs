namespace DittoShell.Domain.Models;

/// <summary>剪贴条目的类型，用于列表上的色点与图标。</summary>
public enum ClipKind
{
    Text,
    Code,
    Link,
    Image,
    File,
    Secret,
    Unknown,

    /// <summary>命令行（git / npm / docker 等开头）。</summary>
    Command,

    /// <summary>长文本（超过 500 字且非代码）。</summary>
    LongText,

    /// <summary>邮箱或电话号码。</summary>
    Contact
}

/// <summary>列表里的一行（不含正文，正文按需另取）。</summary>
public sealed record ClipItem(
    long Id,
    string Preview,
    long GroupId,
    DateTimeOffset CreatedAt,
    ClipKind Kind,
    int CharCount,
    long Crc,
    long LastPasteDate = 0)
{
    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>最后一次粘贴它的时间（Unix 秒），0 表示从未粘贴。</summary>
    public DateTimeOffset? LastPastedAt =>
        LastPasteDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(LastPasteDate) : null;
}

/// <summary>预览面板需要的完整信息。</summary>
public sealed record ClipDetail(
    ClipItem Item,
    string FullText,
    IReadOnlyList<string> Formats,
    long SizeBytes,
    int PasteCount,
    DateTimeOffset? LastPastedAt);
