using System.Text;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>一套分类方案。</summary>
public sealed class TaxonomyScheme
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";          // 「按主题分」「按场景分」
    public string Description { get; init; } = "";   // 一句话说明
    public List<TaxonomyGroup> Groups { get; init; } = new();
}

public sealed class TaxonomyGroup
{
    public int Index { get; set; }          // 1 起（给 AI 输出用）
    public string Path { get; set; } = "";  // 游戏>改枪码
    public string Reason { get; set; } = "";
    public int Estimate { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>分析结果（含多套方案）。</summary>
public sealed class AnalyzeResult
{
    public bool Success { get; init; }
    public string Profile { get; init; } = "";        // 数据画像（人话）
    public List<TaxonomyScheme> Schemes { get; init; } = new();
    public string Recommendation { get; init; } = ""; // AI 推荐哪套 + 理由
    public string? Error { get; init; }
    public int Tokens { get; init; }
    public double Cost { get; init; }
}

/// <summary>
/// 阶段 1：采样分析 + 多方案生成。
/// 会用「历史整理记录」和「高频内容」来调整方案（AC 学习）。
/// </summary>
public sealed class OrganizeAnalyzer
{
    private readonly IClipReader _reader;
    private readonly AiService _ai;
    private readonly OrganizeSampler _sampler;
    private readonly OrganizeService _organize;

    public OrganizeAnalyzer(
        IClipReader reader, AiService ai, OrganizeSampler sampler, OrganizeService organize)
    {
        _reader = reader;
        _ai = ai;
        _sampler = sampler;
        _organize = organize;
    }

    /// <summary>跑一次分析（约 0.02-0.04 元）。</summary>
    public async Task<AnalyzeResult> AnalyzeAsync(
        string? userPrompt = null,
        bool wantMultiScheme = true,
        CancellationToken ct = default)
    {
        if (!_ai.Enabled) return new AnalyzeResult { Success = false, Error = "AI 未启用" };

        // 1) 采样
        var sample = await _sampler.CollectAsync(ct);
        if (sample.Items.Count == 0)
            return new AnalyzeResult { Success = false, Error = "没有可分析的条目" };

        // 2) 现有分组
        var tree = await _reader.GetGroupTreeAsync();
        var groups = new List<string>();
        void Walk(IEnumerable<GroupNode> ns, string prefix)
        {
            foreach (var n in ns)
            {
                var path = prefix.Length == 0 ? n.Name : prefix + ">" + n.Name;
                groups.Add(path);
                Walk(n.Children, path);
            }
        }
        Walk(tree, "");

        // 3) 历史改名习惯（AC 学习 - A）
        var history = await _organize.GetRenameHistoryAsync(20, ct);

        // 4) 拼 payload
        var sb = new StringBuilder();
        sb.AppendLine("=== 数据概况 ===");
        sb.AppendLine(sample.Breakdown);
        sb.AppendLine();

        if (sample.Patterns.Count > 0)
        {
            sb.AppendLine("=== 高频重复模式 ===");
            foreach (var (label, count) in sample.Patterns.Take(20))
                sb.AppendLine($"- {label}");
            sb.AppendLine();
        }

        if (groups.Count > 0)
        {
            sb.AppendLine("=== 现有分组（" + groups.Count + " 个）===");
            sb.AppendLine(string.Join("、", groups.Take(40)));
            sb.AppendLine();
        }

        if (history.Count > 0)
        {
            sb.AppendLine("=== 用户过去的改名习惯（沿用这个风格）===");
            foreach (var (from, to) in history)
                sb.AppendLine($"- 「{from}」→「{to}」");
            sb.AppendLine();
        }

        sb.AppendLine("=== 样本条目（id|日期|类型|内容）===");
        foreach (var it in sample.Items.Take(500))
        {
            var p = (it.Preview ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (p.Length > 70) p = p[..70] + "…";
            sb.Append(it.Id).Append('|')
              .Append(it.CreatedAt.ToString("MM-dd")).Append('|')
              .Append(it.Kind).Append('|')
              .AppendLine(p);
        }

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("=== 用户的额外要求 ===");
            sb.AppendLine(userPrompt);
        }

        // 5) 调 AI
        var system = """
你是剪贴板整理顾问。用户给你他的剪贴板样本，你要：
1. 先分析他的数据画像（什么类型多、有什么重复模式、现有分组够不够）
2. 给出分类方案

输出严格的 JSON（不要 markdown 代码块，不要解释）：
{
  "profile": "两三句话描述这个人的数据特征和使用习惯",
  "schemes": [
    {
      "name": "按主题分",
      "description": "适合找同类内容",
      "groups": [
        {"path": "游戏>改枪码", "reason": "12 条同格式编码", "estimate": 12}
      ]
    },
    {
      "name": "按场景分",
      "description": "适合按使用场景找",
      "groups": [...]
    }
  ],
  "recommendation": "推荐第几套（从 1 开始），一句话理由"
}

要求：
- 分组名沿用用户现有命名风格（参考改名历史）
- 高频重复的内容要单独成组
- 无效/测试/死链类内容统一放进「00 待清理」
- 每组给 estimate（预估条数），总和不超过样本总量
- 最多 12 组，避免碎片化
- 如果只要一套方案，schemes 数组只放一个
""";

        var result = await _ai.RawCompleteAsync(system, sb.ToString(), 384000, 0.3f, ct);

        if (!result.Success)
            return new AnalyzeResult { Success = false, Error = result.Message };

        // 6) 解析
        var parsed = ParseJson(result.Message ?? "");
        if (parsed is null)
            return new AnalyzeResult
            {
                Success = false,
                Error = "AI 返回的格式无法解析",
                Tokens = result.Tokens ?? 0
            };

        return new AnalyzeResult
        {
            Success = true,
            Profile = parsed.Value.Profile,
            Schemes = parsed.Value.Schemes,
            Recommendation = parsed.Value.Recommendation,
            Tokens = result.Tokens ?? 0,
            Cost = (result.Tokens ?? 0) / 1_000_000.0 * 2.0
        };
    }

    private static (string Profile, List<TaxonomyScheme> Schemes, string Recommendation)? ParseJson(string raw)
    {
        try
        {
            var t = raw.Trim();
            // 去掉可能的 ```json 包裹
            if (t.StartsWith("```")) 
            {
                var nl = t.IndexOf('\n');
                if (nl > 0) t = t[(nl + 1)..];
                if (t.EndsWith("```")) t = t[..^3];
            }
            var start = t.IndexOf('{');
            var end = t.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            t = t[start..(end + 1)];

            using var doc = System.Text.Json.JsonDocument.Parse(t);
            var root = doc.RootElement;

            var profile = root.TryGetProperty("profile", out var pe) ? pe.GetString() ?? "" : "";
            var rec = root.TryGetProperty("recommendation", out var re) ? re.GetString() ?? "" : "";

            var schemes = new List<TaxonomyScheme>();
            if (root.TryGetProperty("schemes", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int si = 0;
                foreach (var s in se.EnumerateArray())
                {
                    si++;
                    var name = s.TryGetProperty("name", out var ne) ? ne.GetString() ?? $"方案 {si}" : $"方案 {si}";
                    var desc = s.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "";

                    var gl = new List<TaxonomyGroup>();
                    if (s.TryGetProperty("groups", out var ge) && ge.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int gi = 0;
                        foreach (var g in ge.EnumerateArray())
                        {
                            gi++;
                            gl.Add(new TaxonomyGroup
                            {
                                Index = gi,
                                Path = g.TryGetProperty("path", out var pe2) ? pe2.GetString() ?? "" : "",
                                Reason = g.TryGetProperty("reason", out var re2) ? re2.GetString() ?? "" : "",
                                Estimate = g.TryGetProperty("estimate", out var ee) && ee.ValueKind == System.Text.Json.JsonValueKind.Number
                                    ? ee.GetInt32() : 0
                            });
                        }
                    }

                    schemes.Add(new TaxonomyScheme
                    {
                        Id = "scheme" + si,
                        Name = name,
                        Description = desc,
                        Groups = gl
                    });
                }
            }

            return (profile, schemes, rec);
        }
        catch { return null; }
    }
}
