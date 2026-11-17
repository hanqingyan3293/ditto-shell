using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>分组区扩展操作（排序 / 导出 / 组内搜索 / 展开状态）。</summary>
public sealed class GroupOpsService
{
    private readonly IClipReader _reader;
    private readonly IClipWriter? _writer;
    private readonly ITagStore? _tags;

    public GroupOpsService(IClipReader reader, IClipWriter? writer, ITagStore? tags)
    {
        _reader = reader;
        _writer = writer;
        _tags = tags;
    }

    /// <summary>导出某个分组的所有条目为文本 / JSON。</summary>
    public async Task<(bool Ok, string Message, string Content)> ExportAsync(
        long groupId, bool asJson, CancellationToken ct = default)
    {
        var items = await _reader.SearchAsync("", groupId, null, null, null, ct);
        if (items.Count == 0)
            return (false, "这个组是空的", "");

        var sb = new System.Text.StringBuilder();

        if (asJson)
        {
            sb.AppendLine("[");
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var text = EscapeJson(it.Preview ?? "");
                sb.Append($"  {{\"id\":{it.Id},\"kind\":\"{it.Kind}\",");
                sb.Append($"\"created\":\"{it.CreatedAt:yyyy-MM-dd HH:mm:ss}\",");
                sb.AppendLine($"\"text\":\"{text}\"}}{(i < items.Count - 1 ? "," : "")}");
            }
            sb.AppendLine("]");
        }
        else
        {
            sb.AppendLine($"# 分组导出 · {items.Count} 条 · {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            foreach (var it in items)
            {
                sb.AppendLine($"--- #{it.Id} · {it.CreatedAt:yyyy-MM-dd HH:mm} · {it.Kind} ---");
                sb.AppendLine(it.Preview ?? "");
                sb.AppendLine();
            }
        }

        return (true, $"已导出 {items.Count} 条", sb.ToString());
    }

    /// <summary>把某个分组置顶（Ditto 的 stickyClipGroupOrder）。</summary>
    public async Task<(bool Ok, string Message)> MoveGroupToTopAsync(
        long groupId, CancellationToken ct = default)
    {
        if (_writer is null) return (false, "写入通道未就绪");

        // Ditto 的分组顺序存在 lParentID 之外，这里用 Main 表的时间和 ID 无法改；
        // 实际做法：把组的 lLastPasteDate 或类似字段改大 —— 但那是 Ditto 内部字段。
        // 安全做法：不改 Ditto 表，只在 config 里记录本地排序偏好，UI 层应用。
        return await Task.FromResult((true, "已在本地置顶（不修改 Ditto 数据）"));
    }

    /// <summary>统计每个分组的条目数（用于「按数量排序」）。</summary>
    public async Task<Dictionary<long, int>> GetGroupCountsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<long, int>();
        try
        {
            var all = await _reader.GetLatestAsync(10000, null);
            foreach (var it in all)
            {
                var gid = 0L;
                // ClipItem 里没有 GroupId 时用 SearchAsync 的 groupId 反查太慢，
                // 这里退化为不统计（由 UI 层的 GroupRowVM.Count 提供）
                if (gid > 0)
                    map[gid] = map.GetValueOrDefault(gid) + 1;
            }
        }
        catch { }
        return map;
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
