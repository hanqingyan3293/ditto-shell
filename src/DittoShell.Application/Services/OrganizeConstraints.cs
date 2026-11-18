using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>标签处理模式。</summary>
public enum TagMode
{
    /// <summary>覆盖：AI 重新打所有标签。</summary>
    Overwrite = 0,

    /// <summary>只给没有标签的条目加标签（已有的不动）。</summary>
    OnlyEmpty = 1,

    /// <summary>保留已有标签，追加 AI 的新标签。</summary>
    Append = 2,

    /// <summary>完全不动标签。</summary>
    Skip = 3
}

/// <summary>整理约束（硬开关 + 自然语言解析结果）。</summary>
public sealed class OrganizeConstraints
{
    // ── 硬开关 ──
    public bool ChangeGroups { get; set; } = true;
    public bool ChangeTags { get; set; } = true;
    public TagMode TagMode { get; set; } = TagMode.Overwrite;

    public bool KeepManuallyGrouped { get; set; } = true;
    public bool SkipSecrets { get; set; } = true;
    public bool SkipImages { get; set; } = true;

    /// <summary>隐私条目永远跳过（不可关闭）。</summary>
    public bool SkipPrivate => true;

    // ── AI 解析出来的软约束 ──
    public int MaxTags { get; set; } = 40;
    public List<string> ExtraRules { get; set; } = new();

    /// <summary>用户原始输入（便于排查）。</summary>
    public string RawText { get; set; } = "";

    /// <summary>解析是否成功。</summary>
    public bool ParseOk { get; set; } = true;

    /// <summary>解析说明（给用户看）。</summary>
    public List<string> ParseNotes { get; set; } = new();
}

/// <summary>整理预览（执行前给用户看）。</summary>
public sealed class OrganizePreview
{
    public int TotalCandidates { get; init; }
    public int WillProcess { get; init; }

    /// <summary>跳过原因 → 条目 ID 列表。</summary>
    public Dictionary<string, List<long>> Skipped { get; init; } = new();

    /// <summary>跳过原因 → 前几条样例（内容预览）。</summary>
    public Dictionary<string, List<string>> Samples { get; init; } = new();

    public int GroupCount { get; init; }
    public int TagCount { get; init; }
    public double EstimatedMinutes { get; init; }
    public double EstimatedCost { get; init; }

    public string SkipSummary =>
        Skipped.Count == 0 ? "无跳过" :
        string.Join(" + ", Skipped.Select(kv => $"{kv.Key} {kv.Value.Count}"));
}

/// <summary>
/// 约束解析器：把用户的自然语言转成结构化约束。
/// 只调用 1 次 AI（约 0.001 元），结果给用户确认。
/// </summary>
public sealed class ConstraintParser
{
    private readonly AiService _ai;

    public ConstraintParser(AiService ai) => _ai = ai;

    public async Task<OrganizeConstraints> ParseAsync(
        string userText, OrganizeConstraints baseline, CancellationToken ct = default)
    {
        baseline.RawText = userText ?? "";

        if (string.IsNullOrWhiteSpace(userText))
        {
            baseline.ParseOk = true;
            return baseline;
        }

        if (!_ai.Enabled)
        {
            baseline.ParseOk = false;
            baseline.ParseNotes.Add("AI 未启用，只使用开关设置");
            return baseline;
        }

        const string system = """
你是约束解析器。把用户的中文描述转成 JSON。

只输出 JSON，不要解释，不要 markdown 代码块。

字段：
{
  "changeGroups": true/false,       // 是否允许改分组
  "changeTags": true/false,         // 是否允许改标签
  "tagMode": "overwrite"|"onlyEmpty"|"append"|"skip",
  "keepManuallyGrouped": true/false, // 是否跳过用户手动分过组的条目
  "skipSecrets": true/false,
  "skipImages": true/false,
  "maxTags": 数字,                   // 标签总数上限，20-60
  "extraRules": ["规则1", "规则2"]   // 其他要求（如"允许两级目录"）
}

判断规则：
- "不改标签"/"别动标签" → tagMode = "skip"
- "只给没有标签的加" → tagMode = "onlyEmpty"
- "保留已有标签" → tagMode = "append"
- "重新打标签"/"覆盖" → tagMode = "overwrite"
- "保留我手动分过组的" → keepManuallyGrouped = true
- "别动凭据" → skipSecrets = true
- "标签不要太多"/"控制在X个" → maxTags = X
- 没提到的字段用基线值
""";

        var payload = $"""
用户描述：{userText}

当前开关基线：
changeGroups={baseline.ChangeGroups}
changeTags={baseline.ChangeTags}
tagMode={baseline.TagMode}
keepManuallyGrouped={baseline.KeepManuallyGrouped}
skipSecrets={baseline.SkipSecrets}
skipImages={baseline.SkipImages}
maxTags={baseline.MaxTags}
""";

        var r = await _ai.RawCompleteAsync(system, payload, 4000, 0.1f, ct);

        if (!r.Success || string.IsNullOrWhiteSpace(r.Message))
        {
            baseline.ParseOk = false;
            baseline.ParseNotes.Add("AI 解析失败，只使用开关设置");
            return baseline;
        }

        try
        {
            var json = ExtractJson(r.Message);
            if (json is null) throw new Exception("no json");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("changeGroups", out var cg) &&
                cg.ValueKind is JsonValueKind.True or JsonValueKind.False)
                baseline.ChangeGroups = cg.GetBoolean();

            if (root.TryGetProperty("changeTags", out var ct2) &&
                ct2.ValueKind is JsonValueKind.True or JsonValueKind.False)
                baseline.ChangeTags = ct2.GetBoolean();

            if (root.TryGetProperty("tagMode", out var tm) && tm.ValueKind == JsonValueKind.String)
            {
                baseline.TagMode = tm.GetString() switch
                {
                    "skip" => TagMode.Skip,
                    "onlyEmpty" => TagMode.OnlyEmpty,
                    "append" => TagMode.Append,
                    _ => TagMode.Overwrite
                };
            }

            if (root.TryGetProperty("keepManuallyGrouped", out var km) &&
                km.ValueKind is JsonValueKind.True or JsonValueKind.False)
                baseline.KeepManuallyGrouped = km.GetBoolean();

            if (root.TryGetProperty("skipSecrets", out var ss) &&
                ss.ValueKind is JsonValueKind.True or JsonValueKind.False)
                baseline.SkipSecrets = ss.GetBoolean();

            if (root.TryGetProperty("skipImages", out var si) &&
                si.ValueKind is JsonValueKind.True or JsonValueKind.False)
                baseline.SkipImages = si.GetBoolean();

            if (root.TryGetProperty("maxTags", out var mt) && mt.ValueKind == JsonValueKind.Number)
                baseline.MaxTags = Math.Clamp(mt.GetInt32(), 10, 80);

            baseline.ExtraRules.Clear();
            if (root.TryGetProperty("extraRules", out var er) && er.ValueKind == JsonValueKind.Array)
                foreach (var e in er.EnumerateArray())
                {
                    var v = e.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) baseline.ExtraRules.Add(v);
                }

            baseline.ParseOk = true;
            baseline.ParseNotes.Add("已按你的描述解析约束");
        }
        catch
        {
            baseline.ParseOk = false;
            baseline.ParseNotes.Add("AI 返回格式无法解析，只使用开关设置");
        }

        return baseline;
    }

    private static string? ExtractJson(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t[(nl + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        var s = t.IndexOf('{');
        var e = t.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        return t[s..(e + 1)];
    }
}
