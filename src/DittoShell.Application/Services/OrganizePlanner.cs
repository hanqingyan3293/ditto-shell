using System.Text;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>标签词表项。</summary>
public sealed class TagVocabItem
{
    public string Name { get; set; } = "";
    public int Expected { get; set; }
}

/// <summary>
/// 整理批次执行器：
/// 1) 筛选候选集（按约束跳过）
/// 2) 生成预览（统计 + 样例）
/// 3) 按标签词表分类
/// 4) 先算完再写（一次事务）
/// </summary>
public sealed class OrganizePlanner
{
    private readonly IClipReader _reader;
    private readonly ITagStore? _tags;
    private readonly OrganizeService _organize;

    public OrganizePlanner(IClipReader reader, ITagStore? tags, OrganizeService organize)
    {
        _reader = reader;
        _tags = tags;
        _organize = organize;
    }

    // ═══════════════════════════════════════════
    // 候选集筛选
    // ═══════════════════════════════════════════

    /// <summary>
    /// 按约束筛选候选集。
    /// mode: incremental / full / retry
    /// </summary>
    public async Task<(List<ClipItem> Candidates, OrganizePreview Preview)> BuildCandidatesAsync(
        string mode, OrganizeConstraints c, CancellationToken ct = default)
    {
        var skipped = new Dictionary<string, List<long>>();
        var samples = new Dictionary<string, List<string>>();

        void AddSkip(string reason, ClipItem item)
        {
            if (!skipped.ContainsKey(reason)) { skipped[reason] = new(); samples[reason] = new(); }
            skipped[reason].Add(item.Id);
            if (samples[reason].Count < 5)
            {
                var p = (item.Preview ?? "").Replace('\n', ' ').Trim();
                if (p.Length > 60) p = p[..60] + "…";
                samples[reason].Add($"#{item.Id}  {p}");
            }
        }

        // 1) 全量（含已分组）
        var all = (await _reader.GetLatestAsync(10000, null)).ToList();

        // 2) 隐私（强制跳过）
        var privIds = new HashSet<long>();
        if (_tags is not null)
        {
            try
            {
                var priv = await _tags.GetClipIdsByTagsAsync(
                    new List<string> { "__private" }, false);
                privIds = priv.Select(x => x.ClipId).ToHashSet();
            }
            catch { }
        }
        foreach (var it in all.Where(x => privIds.Contains(x.Id)).ToList())
            AddSkip("隐私（强制）", it);

        // 3) 手动分类的（可选保护）
        HashSet<long> manualIds = new();
        if (c.KeepManuallyGrouped)
        {
            try
            {
                var aiIds = await _organize.GetOrganizedIdsAsync(ct);
                manualIds = all
                    .Where(x => x.GroupId > 0 && !aiIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToHashSet();

                foreach (var it in all.Where(x => manualIds.Contains(x.Id)).ToList())
                    AddSkip("手动分类的", it);
            }
            catch { }
        }

        // 4) 凭据
        if (c.SkipSecrets)
            foreach (var it in all.Where(x => x.Kind == ClipKind.Secret).ToList())
                AddSkip("凭据（你的设置）", it);

        // 5) 图片
        if (c.SkipImages)
            foreach (var it in all.Where(x => x.Kind == ClipKind.Image).ToList())
                AddSkip("图片（AI 看不懂）", it);

        // 6) 按模式收窄
        var excluded = new HashSet<long>();
        foreach (var list in skipped.Values) foreach (var id in list) excluded.Add(id);

        var pool = all.Where(x => !excluded.Contains(x.Id)).ToList();

        switch (mode)
        {
            case "incremental":
            {
                // 只处理没整理过的
                var organized = await _organize.GetOrganizedIdsAsync(ct);
                pool = pool.Where(x => !organized.Contains(x.Id)).ToList();
                break;
            }
            case "retry":
            {
                var stale = await _organize.GetStaleIdsAsync(ct);
                pool = pool.Where(x => stale.Contains(x.Id)).ToList();
                break;
            }
            // full：全量（不做额外过滤）
        }

        var preview = new OrganizePreview
        {
            TotalCandidates = all.Count,
            WillProcess = pool.Count,
            Skipped = skipped,
            Samples = samples,
            EstimatedMinutes = Math.Max(0.5, pool.Count / 1500.0),
            EstimatedCost = pool.Count * 0.00007
        };

        return (pool, preview);
    }

    // ═══════════════════════════════════════════
    // 标签词表注入
    // ═══════════════════════════════════════════

    /// <summary>生成分类阶段的 prompt 片段（含标签词表 + 约束）。</summary>
    public static string BuildClassifySystem(
        TaxonomyScheme scheme, OrganizeConstraints c, IReadOnlyList<TagVocabItem>? vocab)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是剪贴板分类助手。用户已确认分类体系，你只需把条目归类。");
        sb.AppendLine();

        sb.AppendLine("【可用的分组】（只能从这些编号里选）");
        foreach (var g in scheme.Groups.Where(x => x.Enabled))
            sb.AppendLine($"  {g.Index}. {g.Path}");
        sb.AppendLine("  0. （不属于任何一类）");
        sb.AppendLine();

        if (c.ChangeTags && c.TagMode != TagMode.Skip && vocab is { Count: > 0 })
        {
            sb.AppendLine($"【可用的标签】（只能从这 {vocab.Count} 个里选，禁止创造新标签）");
            sb.AppendLine("  " + string.Join("、", vocab.Select(v => v.Name)));
            sb.AppendLine();
        }

        sb.AppendLine("【输出格式】每行一条，不要表头不要解释：");
        if (c.ChangeGroups && c.ChangeTags && c.TagMode != TagMode.Skip)
            sb.AppendLine("  id|分组编号|标签");
        else if (c.ChangeGroups)
            sb.AppendLine("  id|分组编号");
        else if (c.ChangeTags && c.TagMode != TagMode.Skip)
            sb.AppendLine("  id|标签");
        else
            sb.AppendLine("  id|0");

        sb.AppendLine();
        sb.AppendLine("【规则】");
        sb.AppendLine("- 分组编号必须是上面列出的数字");
        sb.AppendLine("- 不属于任何一类写 0");
        if (c.ChangeTags && c.TagMode != TagMode.Skip)
        {
            if (vocab is { Count: > 0 })
                sb.AppendLine("- 标签必须从词表里选，不能自创");
            else
                sb.AppendLine("- 标签 2-8 个汉字，描述内容");

            if (c.TagMode == TagMode.OnlyEmpty)
                sb.AppendLine("- 这条已有标签就留空，没标签才填");
            else if (c.TagMode == TagMode.Append)
                sb.AppendLine("- 只填新增标签，已有的不用重复");
        }

        foreach (var rule in c.ExtraRules)
            sb.AppendLine("- " + rule);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════
    // 应用（先算完再写）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 把算完的结果一次性写入（单事务）。
    /// 中途失败会整体回滚。
    /// </summary>
    public async Task<(bool Ok, int Done, string Message)> ApplyAllAsync(
        IReadOnlyList<OrganizeItem> results,
        Action<int, int, string>? progress = null,
        CancellationToken ct = default)
    {
        if (results.Count == 0) return (true, 0, "没有要应用的结果");

        try
        {
            var (done, msg) = await _organize.ApplyAsync(results, progress, ct);
            return (true, done, msg);
        }
        catch (Exception ex)
        {
            return (false, 0, "写入失败：" + ex.Message);
        }
    }
}
