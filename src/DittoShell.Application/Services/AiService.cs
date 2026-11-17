using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>
/// AI 用例编排：长度限制 → 本地脱敏 → 调用 provider → 结果处理。
/// 默认关闭；未配置 Key 时直接拒绝，不发任何网络请求。
/// </summary>
public sealed class AiService
{
    private const int MaxInputChars = 8000;
    private static readonly string NL = Environment.NewLine;

    private readonly IAiProvider _provider;
    private readonly SensitiveDetector _detector;
    private readonly Func<bool> _isEnabled;

    public AiService(IAiProvider provider, SensitiveDetector detector, Func<bool>? isEnabled = null)
    {
        _provider = provider;
        _detector = detector;
        _isEnabled = isEnabled ?? (() => true);
    }

    public bool Enabled => _isEnabled();

    /// <summary>生成一句话摘要。</summary>
    public async Task<OpResult> SummarizeAsync(string text, CancellationToken ct = default)
    {
        if (!Enabled) return OpResult.Fail("AI 未启用（设置 → AI）");
        if (string.IsNullOrWhiteSpace(text)) return OpResult.Fail("这条没有文本内容");

        var (payload, blocked) = Prepare(text);
        if (blocked) return OpResult.Fail("这条包含疑似凭据，已阻止发送到云端");

        var result = await _provider.CompleteAsync(new AiRequest(
            "你是剪贴板内容摘要助手。用一句中文概括这段内容的核心，不超过 40 字，不要加引号或解释。",
            payload, 384000, 0.2f), ct);

        return result.Success ? OpResult.Ok(result.Content ?? "") : OpResult.Fail(result.Error ?? "调用失败");
    }

    /// <summary>建议 1-3 个标签。</summary>
    public async Task<OpResult> SuggestTagsAsync(string text, CancellationToken ct = default)
    {
        if (!Enabled) return OpResult.Fail("AI 未启用（设置 → AI）");
        if (string.IsNullOrWhiteSpace(text)) return OpResult.Fail("这条没有文本内容");

        var (payload, blocked) = Prepare(text);
        if (blocked) return OpResult.Fail("这条包含疑似凭据，已阻止发送到云端");

        var result = await _provider.CompleteAsync(new AiRequest(
            "给这段内容建议 1 到 3 个简短中文标签。只输出标签，用英文逗号分隔，不要序号、不要解释、不要空格。",
            payload, 384000, 0.2f), ct);

        if (!result.Success) return OpResult.Fail(result.Error ?? "调用失败");

        var tags = (result.Content ?? "")
            .Split(new[] { ',', '，', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length is > 0 and <= 16)
            .Distinct()
            .Take(3)
            .ToList();

        return tags.Count == 0 ? OpResult.Fail("AI 没有给出可用标签") : OpResult.Ok(string.Join(",", tags));
    }

    /// <summary>
    /// 批量整理建议：让 AI 给每条内容建议「分组路径 + 标签」。
    /// 输出格式每行一条：编号|父组&gt;子组|标签
    /// </summary>
    public async Task<OpResult> OrganizeAsync(
        IReadOnlyList<(long Id, string Text)> clips,
        IReadOnlyList<string> existingGroups,
        string? userPrompt = null,
        CancellationToken ct = default)
    {
        if (!Enabled) return OpResult.Fail("AI 未启用（设置 → AI）");
        if (clips.Count == 0) return OpResult.Fail("没有需要整理的条目");

        var list = clips.Take(40).ToList();
        var sb = new System.Text.StringBuilder();

        foreach (var (id, text) in list)
        {
            var one = (text ?? "");
            one = one.Replace((char)13, ' ').Replace((char)10, ' ').Replace((char)9, ' ').Trim();
            if (one.Length > 100) one = one[..100];
            sb.Append(id).Append(" | ").AppendLine(one);
        }

        var groupHint = existingGroups.Count > 0
            ? "现有分组（优先复用，可用 父组>子组 两级）：" + string.Join("、", existingGroups.Take(24))
            : "目前没有分组，可以自己起名字。";

        var userIntent = string.IsNullOrWhiteSpace(userPrompt)
            ? "按内容的主题和用途分类。"
            : "用户的整理要求：" + userPrompt;

        var payload = "待整理条目（编号 | 内容）：" + NL + sb + NL
                    + groupHint + NL
                    + userIntent;

        var result = await _provider.CompleteAsync(new AiRequest(
            "你是剪贴板整理助手。对每条内容输出一行，格式严格为：编号|分组路径|标签。" +
            "分组路径最多两级，用 > 分隔（例如 编程>前端）。标签 2-8 个汉字。" +
            "同主题的条目尽量归到同一个分组。不要表头、不要序号、不要解释。",
            payload, 384000, 0.2f), ct);

        if (!result.Success) return OpResult.Fail(result.Error ?? "调用失败");

        return OpResult.Ok(result.Content ?? "");
    }

    /// <summary>
    /// 语义搜索：把查询和候选列表交给 AI，让它挑出最相关的条目 ID。
    /// 候选只发前 60 个，避免 token 爆炸。
    /// </summary>
    public async Task<OpResult> RankAsync(string query,
                                          IReadOnlyList<(long Id, string Text)> candidates,
                                          CancellationToken ct = default)
    {
        if (!Enabled) return OpResult.Fail("AI 未启用（设置 → AI）");
        if (string.IsNullOrWhiteSpace(query)) return OpResult.Fail("请输入查询内容");
        if (candidates.Count == 0) return OpResult.Fail("没有可检索的条目");

        var list = candidates.Take(60).ToList();
        var sb = new System.Text.StringBuilder();

        foreach (var (id, text) in list)
        {
            var one = (text ?? "")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();

            if (one.Length > 80) one = one[..80];
            sb.Append(id).Append(": ").AppendLine(one);
        }

        var payload = "候选条目（格式 编号: 内容）：" + NL + sb + NL + "用户的查询意图：" + query;

        var result = await _provider.CompleteAsync(new AiRequest(
            "你是剪贴板检索助手。从候选条目里挑出与用户意图最相关的，最多 8 条。" +
            "只输出编号，用英文逗号分隔，按相关度从高到低。没有相关项就输出 NONE。不要解释。",
            payload, 384000, 0.1f), ct);

        if (!result.Success) return OpResult.Fail(result.Error ?? "调用失败");

        var raw = (result.Content ?? "").Trim();
        if (raw.Equals("NONE", StringComparison.OrdinalIgnoreCase) || raw.Length == 0)
            return OpResult.Ok("");

        var ids = raw
            .Split(new[] { ',', '，', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x.Trim(), out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .Take(8)
            .ToList();

        return OpResult.Ok(string.Join(",", ids));
    }

    /// <summary>原样返回 AI 的文本（不走 OpResult 包装），带 token 统计。</summary>
    public async Task<(bool Success, string? Message, int? Tokens)> RawCompleteAsync(
        string systemPrompt, string userContent, int maxTokens, float temperature,
        CancellationToken ct = default)
    {
        if (!Enabled) return (false, "AI 未启用", null);

        var r = await _provider.CompleteAsync(
            new AiRequest(systemPrompt, userContent, maxTokens, temperature), ct);

        if (!r.Success) return (false, r.Error, null);

        // AiResult 没有 tokens 字段，用长度估
        var est = (systemPrompt.Length + userContent.Length) / 3
                + (r.Content?.Length ?? 0) / 2;

        return (true, r.Content, est);
    }

    public Task<(bool Ok, string Message, int ElapsedMs)> TestAsync(CancellationToken ct = default)
        => _provider.TestAsync(ct);

    public Task<(bool Ok, List<string> Models, string? Error)> ListModelsAsync(CancellationToken ct = default)
        => _provider.ListModelsAsync(ct);

    /// <summary>发送前的统一处理：截断 + 脱敏判定。</summary>
    private (string Payload, bool Blocked) Prepare(string text)
    {
        var truncated = text.Length > MaxInputChars ? text[..MaxInputChars] + NL + "…（已截断）" : text;

        var hits = _detector.Detect(truncated);
        if (hits.Any(h => h.Kind is "API Key" or "GitHub 令牌" or "AWS 密钥" or "私钥" or "JWT" or "数据库连接串"))
            return (truncated, true);

        return (_detector.Mask(truncated), false);
    }
}
