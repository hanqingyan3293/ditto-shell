using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>采样结果。</summary>
public sealed class SampleResult
{
    public List<ClipItem> Items { get; init; } = new();

    /// <summary>各来源的分布说明（给 AI 看）。</summary>
    public string Breakdown { get; init; } = "";

    /// <summary>重复模式（同内容出现多次的）。</summary>
    public List<(string Preview, int Count)> Patterns { get; init; } = new();
}

/// <summary>
/// 分层采样器：为 AI 分析准备代表性样本。
/// 不用百分比（总量一大就爆），固定 500 条上限，靠分层保证覆盖。
/// </summary>
public sealed class OrganizeSampler
{
    private readonly IClipReader _reader;

    /// <summary>总采样上限。</summary>
    public int MaxSamples { get; set; } = 500;

    public OrganizeSampler(IClipReader reader) => _reader = reader;

    /// <summary>采样：类型 / 时间 / 频率 / 已有分组，四层合并去重。</summary>
    public async Task<SampleResult> CollectAsync(CancellationToken ct = default)
    {
        var all = (await _reader.SearchAsync("", null, null, null, null, ct)).ToList();
        if (all.Count == 0)
            return new SampleResult { Breakdown = "没有条目" };

        var picked = new Dictionary<long, ClipItem>();
        var layers = new List<string>();

        // ── 第 1 层：按类型各 20 条 ──
        var kinds = all.GroupBy(x => x.Kind).ToList();
        foreach (var g in kinds)
        {
            foreach (var it in g.OrderByDescending(x => x.CreatedAt).Take(20))
                picked[it.Id] = it;
        }
        layers.Add($"类型覆盖 {kinds.Count} 类 × 20 条");

        // ── 第 2 层：最近 30 天每天 2 条 ──
        var from = DateTime.Today.AddDays(-30);
        var recent = all.Where(x => x.CreatedAt.LocalDateTime.Date >= from)
                        .GroupBy(x => x.CreatedAt.LocalDateTime.Date);
        foreach (var day in recent)
        {
            foreach (var it in day.OrderByDescending(x => x.CreatedAt).Take(2))
                picked[it.Id] = it;
        }
        layers.Add($"最近 30 天每日 2 条");

        // ── 第 3 层：频率聚类（找重复模式） ──
        var patterns = FindPatterns(all);
        foreach (var p in patterns)
        {
            // 每个模式取 1 条代表
            var rep = all.FirstOrDefault(x =>
                Normalize(x.Preview).StartsWith(p.Key, StringComparison.Ordinal));
            if (rep is not null) picked[rep.Id] = rep;
        }
        layers.Add($"重复模式 {patterns.Count} 组");

        // ── 第 4 层：已有分组的代表各 3 条 ──
        var byGroup = all.Where(x => x.GroupId > 0).GroupBy(x => x.GroupId);
        foreach (var g in byGroup)
        {
            foreach (var it in g.OrderByDescending(x => x.CreatedAt).Take(3))
                picked[it.Id] = it;
        }
        layers.Add($"按分组代表 {byGroup.Count()} 组 × 3 条");

        // ── 合并 + 截断 ──
        var items = picked.Values
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxSamples)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("总量 ").Append(all.Count).Append(" 条；采样 ").Append(items.Count).Append(" 条");
        sb.Append("（").Append(string.Join(" + ", layers)).Append("）");

        return new SampleResult
        {
            Items = items,
            Breakdown = sb.ToString(),
            Patterns = patterns.Select(p => (p.Label, p.Count)).ToList()
        };
    }

    /// <summary>
    /// 找重复模式：把内容归一化后取前 20 字做 key，
    /// 出现 ≥3 次的算一个模式。
    /// </summary>
    private List<(string Key, string Label, int Count)> FindPatterns(IReadOnlyList<ClipItem> all)
    {
        var groups = new Dictionary<string, (int Count, string Sample)>();

        foreach (var it in all)
        {
            var norm = Normalize(it.Preview);
            if (norm.Length < 6) continue;

            // 用前 20 字做模式键
            var key = norm.Length > 20 ? norm[..20] : norm;
            if (groups.TryGetValue(key, out var cur))
                groups[key] = (cur.Count + 1, cur.Sample);
            else
                groups[key] = (1, it.Preview ?? "");
        }

        return groups
            .Where(g => g.Value.Count >= 3)
            .OrderByDescending(g => g.Value.Count)
            .Take(40)
            .Select(g => (
                Key: g.Key,
                Label: g.Value.Count + " 条相似：" +
                    (g.Value.Sample.Length > 40 ? g.Value.Sample[..40] : g.Value.Sample),
                Count: g.Value.Count))
            .ToList();
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        // 去掉数字（避免 "参数1" "参数2" 被当成不同模式）
        var chars = t.Where(c => !char.IsDigit(c)).ToArray();
        return new string(chars).Trim();
    }
}
