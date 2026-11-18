using System.Security.Cryptography;
using System.Text;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>一条整理建议。</summary>
public sealed class OrganizeItem
{
    public long ClipId { get; set; }
    public string Preview { get; set; } = "";

    /// <summary>分组路径，例如「工作」或「工作&gt;项目A」。</summary>
    public string GroupPath { get; set; } = "";
    public string Tag { get; set; } = "";
    public bool Selected { get; set; } = true;

    public string GroupDisplay => GroupPath.Replace(">", " › ");
    public string TagDisplay => string.IsNullOrWhiteSpace(Tag) ? "—" : "#" + Tag;
    public bool IsDone { get; set; }
}

/// <summary>
/// 把 AI 的整理建议应用到 Ditto 库：支持嵌套子组、增量跳过已整理内容。
/// </summary>
public sealed class OrganizeService
{
    private readonly IClipReader _reader;
    private readonly IClipWriter _writer;
    private readonly ITagStore _tags;
    private readonly IOrganizeLogStore? _log;

    public OrganizeService(IClipReader reader, IClipWriter writer, ITagStore tags, IOrganizeLogStore? log = null)
    {
        _reader = reader;
        _writer = writer;
        _tags = tags;
        _log = log;
    }

    /// <summary>内容指纹（用来判断条目是否变过）。</summary>
    public static string Hash(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>过滤掉已经整理过的条目（增量上传，省 token）。</summary>
    /// <summary>
    /// 用户过去的改名习惯（AI 建议的分组名 → 用户实际用的名字）。
    /// 用于让 AI 沿用一致的命名风格。
    /// </summary>
    public async Task<List<(string From, string To)>> GetRenameHistoryAsync(
        int limit = 20, CancellationToken ct = default)
    {
        if (_log is null) return new List<(string, string)>();
        try { return await _log.GetRenameHistoryAsync(limit, ct); }
        catch { return new List<(string, string)>(); }
    }

    /// <summary>清空整理记录（"全部重来"模式用）。</summary>
    public async Task ClearLogAsync(CancellationToken ct = default)
    {
        if (_log is null) return;
        await _log.ClearAllAsync(ct);
    }

    /// <summary>整理过但内容改过的条目 ID（需要再整理）。</summary>
    public async Task<List<long>> GetStaleIdsAsync(CancellationToken ct = default)
    {
        if (_log is null) return new List<long>();
        return await _log.GetStaleIdsAsync(ct);
    }

    public async Task<(List<ClipItem> Fresh, int Skipped)> FilterNewAsync(
        IReadOnlyList<ClipItem> candidates, CancellationToken ct = default)
    {
        if (_log is null) return (candidates.ToList(), 0);

        var done = await _log.GetOrganizedIdsAsync(ct);
        var fresh = candidates.Where(c => !done.Contains(c.Id)).ToList();
        return (fresh, candidates.Count - fresh.Count);
    }

    /// <summary>已整理过的条目 ID 集合（用于识别用户手动分类的条目）。</summary>
    public async Task<HashSet<long>> GetOrganizedIdsAsync(CancellationToken ct = default)
    {
        if (_log is null) return new HashSet<long>();
        try { return await _log.GetOrganizedIdsAsync(ct); }
        catch { return new HashSet<long>(); }
    }

    public Task<int> GetOrganizedCountAsync(CancellationToken ct = default)
        => _log?.GetOrganizedCountAsync(ct) ?? Task.FromResult(0);

    /// <summary>解析 AI 输出。支持三级路径：父&gt;子。</summary>
    public List<OrganizeItem> Parse(string raw, IReadOnlyList<ClipItem> candidates)
    {
        var map = candidates.ToDictionary(c => c.Id);
        var list = new List<OrganizeItem>();

        foreach (var line in (raw ?? "").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            var parts = text.Split('|');
            if (parts.Length < 2) continue;

            if (!long.TryParse(parts[0].Trim(), out var id)) continue;
            if (!map.TryGetValue(id, out var clip)) continue;

            var path = parts[1].Trim().TrimStart('#');
            var tag = parts.Length >= 3 ? parts[2].Trim().TrimStart('#') : "";

            // 清理路径：去掉空段、限制每段长度
            var segs = path.Split('>', StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim())
                           .Where(s => s.Length is > 0 and <= 12)
                           .Take(3)
                           .ToArray();

            if (segs.Length == 0) continue;

            list.Add(new OrganizeItem
            {
                ClipId = id,
                Preview = clip.Preview,
                GroupPath = string.Join(">", segs),
                Tag = tag.Length <= 12 ? tag : tag[..12]
            });
        }

        return list;
    }

    /// <summary>应用选中的建议（自动建父组/子组）。</summary>
    /// <summary>把一批整理结果记进 organize_log（应用后调用）。</summary>
    public async Task MarkDoneAsync(OrganizeItem item, CancellationToken ct = default)
    {
        if (_log is null) return;
        try
        {
            var hash = (item.Preview ?? "").GetHashCode().ToString("X");
            await _log.MarkOrganizedAsync(
                item.ClipId, hash, item.GroupPath, item.Tag, ct);
        }
        catch { }
    }

    /// <summary>批量记录。</summary>
    public async Task MarkDoneBatchAsync(
        IEnumerable<OrganizeItem> items, CancellationToken ct = default)
    {
        foreach (var it in items) await MarkDoneAsync(it, ct);
    }

    /// <summary>
    /// 应用整理结果（批量事务版）。
    /// progress: (已完成, 总数, 阶段说明)
    /// </summary>
    public async Task<(int Done, string Message)> ApplyAsync(
        IReadOnlyList<OrganizeItem> items,
        Action<int, int, string>? progress = null,
        CancellationToken ct = default)
    {
        var picked = items.Where(x => x.Selected && !x.IsDone).ToList();
        if (picked.Count == 0) return (0, "没有勾选任何条目");

        // 现有分组：路径 -> id
        var tree = await _reader.GetGroupTreeAsync();
        var pathMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Collect(IEnumerable<GroupNode> nodes, string prefix)
        {
            foreach (var n in nodes)
            {
                var full = prefix.Length == 0 ? n.Name : prefix + ">" + n.Name;
                if (!pathMap.ContainsKey(full)) pathMap[full] = n.Id;
                Collect(n.Children, full);
            }
        }
        Collect(tree, "");

        int done = 0;
        var errors = new List<string>();
        var tagBatch = new List<(long ClipId, string Tag)>();
        var logBatch = new List<(long ClipId, string Hash, string GroupPath, string Tag)>();
        var movedCount = 0;
        var totalGroups = picked.GroupBy(x => x.GroupPath, StringComparer.OrdinalIgnoreCase).Count();
        var groupIndex = 0;

        foreach (var group in picked.GroupBy(x => x.GroupPath, StringComparer.OrdinalIgnoreCase))
        {
            long groupId = 0;
            var segs = group.Key.Split('>');
            var path = "";

            try
            {
                foreach (var seg in segs)
                {
                    path = path.Length == 0 ? seg : path + ">" + seg;

                    if (pathMap.TryGetValue(path, out var existing))
                    {
                        groupId = existing;
                        continue;
                    }

                    // 父级 id
                    long parentId = -1;
                    if (path.Contains('>'))
                    {
                        var parentPath = path[..path.LastIndexOf('>')];
                        if (pathMap.TryGetValue(parentPath, out var pid)) parentId = pid;
                    }

                    groupId = await _writer.CreateGroupAsync(seg, parentId, ct);
                    pathMap[path] = groupId;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"建分组「{group.Key}」失败：{ex.Message}");
                continue;
            }

            var ids = group.Select(x => x.ClipId).ToList();

            try
            {
                await _writer.MoveToGroupAsync(ids, groupId, ct);
                movedCount += ids.Count;
            }
            catch (Exception ex)
            {
                errors.Add($"移动到「{group.Key}」失败：{ex.Message}");
                groupIndex++;
                continue;
            }

            // 只收集，不落库（最后批量写）
            foreach (var item in group)
            {
                if (!string.IsNullOrWhiteSpace(item.Tag))
                    tagBatch.Add((item.ClipId, item.Tag));

                logBatch.Add((item.ClipId, Hash(item.Preview), item.GroupPath, item.Tag));
                item.IsDone = true;
                done++;
            }

            groupIndex++;
            progress?.Invoke(done, picked.Count, $"移动分组 {groupIndex}/{totalGroups}（已处理 {done} 条）");
        }

        // ── 批量写标签（单事务）──
        if (tagBatch.Count > 0)
        {
            progress?.Invoke(done, picked.Count, $"写入 {tagBatch.Count} 个标签…");
            try { await _tagsAddBatch(tagBatch, ct); }
            catch (Exception ex) { errors.Add($"标签写入失败：{ex.Message}"); }
        }

        // ── 批量记日志（单事务）──
        if (logBatch.Count > 0)
        {
            progress?.Invoke(done, picked.Count, $"记录 {logBatch.Count} 条整理日志…");
            try
            {
                if (_log is not null)
                    await _log.MarkOrganizedBatchAsync(logBatch, ct);
            }
            catch (Exception ex) { errors.Add($"日志写入失败：{ex.Message}"); }
        }

        progress?.Invoke(done, picked.Count, "完成");

        var msg = errors.Count == 0
            ? $"已整理 {done} 条（{movedCount} 条移动分组，{tagBatch.Count} 个标签）"
            : $"已整理 {done} 条，{errors.Count} 项失败：{string.Join("；", errors.Take(2))}";

        return (done, msg);
    }

    /// <summary>批量加标签。</summary>
    private async Task _tagsAddBatch(
        IReadOnlyList<(long ClipId, string Tag)> batch, CancellationToken ct)
    {
        if (_tags is null) return;
        await _tags.AddTagsBatchAsync(batch, ct);
    }
}
