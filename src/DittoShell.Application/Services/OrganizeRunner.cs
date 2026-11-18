using System.Collections.Concurrent;
using System.Text;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>
/// 阶段 3：按用户确认的方案批量分类。
/// 关键：taxonomy 注入每批 prompt，保证批次间一致。
/// </summary>
public sealed class OrganizeRunner
{
    private readonly IClipReader _reader;
    private readonly AiService _ai;
    private readonly OrganizeService _organize;

    public int BatchSize { get; set; } = 60;
    public int InitialConcurrency { get; set; } = 4;

    /// <summary>最近几批的失败原因（排查用）。</summary>
    public List<string> LastErrors { get; } = new();
    public int MaxConcurrency { get; set; } = 8;

    public OrganizeRunner(IClipReader reader, AiService ai, OrganizeService organize)
    {
        _reader = reader;
        _ai = ai;
        _organize = organize;
    }

    /// <summary>按方案跑全量分类。</summary>
    public async Task<List<OrganizeItem>> RunAsync(
        TaxonomyScheme scheme,
        IReadOnlyList<ClipItem> candidates,
        string? userPrompt,
        Action<OrganizeProgress>? onProgress,
        CancellationToken ct = default)
    {
        var progress = new OrganizeProgress { Phase = "准备中…", Total = candidates.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        onProgress?.Invoke(progress);

        if (candidates.Count == 0)
        {
            progress.Phase = "没有需要分类的条目";
            return new List<OrganizeItem>();
        }

        // 拼分类体系（编号列表，省 token 且不会拼错）
        var schemeText = new StringBuilder();
        schemeText.AppendLine("=== 用户确认的分类体系 ===");
        foreach (var g in scheme.Groups.Where(x => x.Enabled))
            schemeText.AppendLine($"{g.Index}. {g.Path}");
        schemeText.AppendLine("0. （不属于以上任何一类）");
        schemeText.AppendLine();

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            schemeText.AppendLine("=== 用户的额外要求 ===");
            schemeText.AppendLine(userPrompt);
            schemeText.AppendLine();
        }

        // 切批
        var batches = new List<List<ClipItem>>();
        for (int i = 0; i < candidates.Count; i += BatchSize)
            batches.Add(candidates.Skip(i).Take(BatchSize).ToList());

        progress.TotalBatches = batches.Count;
        progress.Phase = $"共 {batches.Count} 批";
        onProgress?.Invoke(progress);

        var results = new ConcurrentBag<OrganizeItem>();
        int concurrency = InitialConcurrency;
        int successCount = 0, failCount = 0, batchDone = 0, doneCount = 0, consecutiveFail = 0;

        using var sem = new SemaphoreSlim(concurrency, MaxConcurrency);

        var tasks = batches.Select(async batch =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var payload = new StringBuilder(schemeText.ToString());
                payload.AppendLine("=== 待分类条目（id|内容）===");
                foreach (var it in batch)
                {
                    var p = (it.Preview ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                    if (p.Length > 100) p = p[..100];
                    payload.Append(it.Id).Append('|').AppendLine(p);
                }

                var sys = """
你是剪贴板分类助手。用户已经确认了分类体系，你只需把条目归到对应编号。

输出格式（每行一条，不要表头不要解释）：
id|编号|标签

- 编号必须是上面列出的数字，不属于任何一类写 0
- 标签 2-8 个汉字，描述内容
- 无效/测试/死链类一律编号 0，标签用「无效」
""";

                // max_tokens 要给足：deepseek-flash 的 reasoning 会吃掉一大半
                var r = await _ai.RawCompleteAsync(sys, payload.ToString(), 384000, 0.2f, ct);

                // 失败重试一次（多半是 reasoning 占满导致 content 为空）
                if (!r.Success)
                {
                    await Task.Delay(800, ct);
                    r = await _ai.RawCompleteAsync(sys, payload.ToString(), 384000, 0.2f, ct);
                }

                if (r.Success && !string.IsNullOrWhiteSpace(r.Message))
                {
                    var parsed = ParseByIndex(r.Message, batch, scheme);
                    foreach (var p in parsed) results.Add(p);
                    Interlocked.Increment(ref successCount);
                    Interlocked.Exchange(ref consecutiveFail, 0);
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                    var cf = Interlocked.Increment(ref consecutiveFail);
                    if (cf >= 3 && concurrency > 1) concurrency = Math.Max(1, concurrency / 2);

                    lock (LastErrors)
                    {
                        if (LastErrors.Count < 10)
                            LastErrors.Add($"{batch.Count} 条批失败：{r.Message ?? "无返回"}");
                    }
                }

                Interlocked.Increment(ref batchDone);
                Interlocked.Add(ref doneCount, batch.Count);

                var sc = Volatile.Read(ref successCount);
                if (sc > 0 && sc % 5 == 0 && concurrency < MaxConcurrency)
                    concurrency = Math.Min(MaxConcurrency, concurrency + 2);

                progress.Batch = Volatile.Read(ref batchDone);
                progress.Done = Volatile.Read(ref doneCount);
                progress.Success = sc;
                progress.Failed = Volatile.Read(ref failCount);
                progress.Concurrency = concurrency;
                progress.ElapsedSec = sw.Elapsed.TotalSeconds;
                progress.Phase = $"第 {progress.Batch}/{batches.Count} 批";

                lock (progress) { onProgress?.Invoke(progress); }
            }
            finally { sem.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { progress.Phase = "已取消"; }

        sw.Stop();
        progress.ElapsedSec = sw.Elapsed.TotalSeconds;
        progress.Phase = $"完成：{results.Count} 条，用时 {sw.Elapsed.TotalMinutes:F1} 分钟";
        onProgress?.Invoke(progress);

        return results.OrderBy(x => x.ClipId).ToList();
    }

    /// <summary>
    /// 按约束 + 标签词表分类（新版本）。
    /// 只算不写——结果交给调用方决定何时应用。
    /// </summary>
    public async Task<List<OrganizeItem>> RunWithConstraintsAsync(
        TaxonomyScheme scheme,
        IReadOnlyList<ClipItem> candidates,
        IReadOnlyList<TagVocabItem> vocab,
        OrganizeConstraints c,
        string? userPrompt,
        Action<OrganizeProgress>? onProgress,
        CancellationToken ct = default)
    {
        var progress = new OrganizeProgress { Phase = "准备中…", Total = candidates.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        onProgress?.Invoke(progress);

        if (candidates.Count == 0)
        {
            progress.Phase = "没有需要分类的条目";
            return new List<OrganizeItem>();
        }

        // 用 planner 生成 system prompt（含词表 + 约束）
        var sys = OrganizePlanner.BuildClassifySystem(scheme, c, vocab);

        var schemeText = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            schemeText.AppendLine("=== 用户的额外要求 ===");
            schemeText.AppendLine(userPrompt);
            schemeText.AppendLine();
        }

        // 切批
        var batches = new List<List<ClipItem>>();
        for (int i = 0; i < candidates.Count; i += BatchSize)
            batches.Add(candidates.Skip(i).Take(BatchSize).ToList());

        progress.TotalBatches = batches.Count;
        progress.Phase = $"共 {batches.Count} 批";
        onProgress?.Invoke(progress);

        var results = new System.Collections.Concurrent.ConcurrentBag<OrganizeItem>();
        int concurrency = InitialConcurrency;
        int successCount = 0, failCount = 0, batchDone = 0, doneCount = 0;
        int consecutiveFail = 0;

        using var sem = new SemaphoreSlim(concurrency, MaxConcurrency);

        var tasks = batches.Select(async batch =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var payload = new System.Text.StringBuilder(schemeText.ToString());
                payload.AppendLine("=== 待分类条目（id|内容）===");
                foreach (var it in batch)
                {
                    var p = (it.Preview ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                    if (p.Length > 100) p = p[..100];
                    payload.Append(it.Id).Append('|').AppendLine(p);
                }

                var r = await _ai.RawCompleteAsync(sys, payload.ToString(), 384000, 0.2f, ct);

                if (!r.Success)
                {
                    await Task.Delay(800, ct);
                    r = await _ai.RawCompleteAsync(sys, payload.ToString(), 384000, 0.2f, ct);
                }

                if (r.Success && !string.IsNullOrWhiteSpace(r.Message))
                {
                    var parsed = ParseWithConstraints(r.Message, batch, scheme, vocab, c);
                    foreach (var p in parsed) results.Add(p);
                    Interlocked.Increment(ref successCount);
                    Interlocked.Exchange(ref consecutiveFail, 0);
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                    var cf = Interlocked.Increment(ref consecutiveFail);
                    if (cf >= 3 && concurrency > 1) concurrency = Math.Max(1, concurrency / 2);

                    lock (LastErrors)
                    {
                        if (LastErrors.Count < 10)
                            LastErrors.Add($"{batch.Count} 条批失败：{r.Message ?? "无返回"}");
                    }
                }

                Interlocked.Increment(ref batchDone);
                Interlocked.Add(ref doneCount, batch.Count);

                var sc = Volatile.Read(ref successCount);
                if (sc > 0 && sc % 5 == 0 && concurrency < MaxConcurrency)
                    concurrency = Math.Min(MaxConcurrency, concurrency + 2);

                progress.Batch = Volatile.Read(ref batchDone);
                progress.Done = Volatile.Read(ref doneCount);
                progress.Success = sc;
                progress.Failed = Volatile.Read(ref failCount);
                progress.Concurrency = concurrency;
                progress.ElapsedSec = sw.Elapsed.TotalSeconds;
                progress.Phase = $"第 {progress.Batch}/{batches.Count} 批";

                lock (progress) { onProgress?.Invoke(progress); }
            }
            finally { sem.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { progress.Phase = "已取消"; }

        sw.Stop();
        progress.ElapsedSec = sw.Elapsed.TotalSeconds;
        progress.Phase = $"完成：{results.Count} 条";
        onProgress?.Invoke(progress);

        return results.OrderBy(x => x.ClipId).ToList();
    }

    /// <summary>解析 id|分组编号|标签（按约束处理）。</summary>
    private static List<OrganizeItem> ParseWithConstraints(
        string raw, IReadOnlyList<ClipItem> batch, TaxonomyScheme scheme,
        IReadOnlyList<TagVocabItem> vocab, OrganizeConstraints c)
    {
        var list = new List<OrganizeItem>();
        var byId = batch.ToDictionary(x => x.Id, x => x);
        var byIndex = scheme.Groups.Where(g => g.Enabled).ToDictionary(g => g.Index, g => g.Path);
        var vocabSet = vocab.Select(v => v.Name).ToHashSet();
        var vocabList = vocab.ToList();

        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("```")) continue;
            if (t.StartsWith("- ")) t = t[2..].Trim();
            if (t.StartsWith("* ")) t = t[2..].Trim();
            if (t.StartsWith("|")) t = t.TrimStart('|').Trim();
            t = t.TrimEnd('|').Trim();
            if (t.Length == 0) continue;

            var parts = t.Split('|');
            if (parts.Length < 2) parts = t.Split(',');
            if (parts.Length < 2) parts = t.Split('\t');
            if (parts.Length < 2) continue;

            if (!long.TryParse(parts[0].Trim(), out var id)) continue;
            if (!byId.TryGetValue(id, out var clip)) continue;

            // 解析分组
            string path = "";
            bool groupChanged = false;
            if (c.ChangeGroups && int.TryParse(parts[1].Trim(), out var idx))
            {
                path = idx == 0 ? "00 待清理" : byIndex.GetValueOrDefault(idx, "00 待清理");
                groupChanged = true;
            }

            // 解析标签（必须从词表里选）
            string tag = "";
            if (c.ChangeTags && c.TagMode != TagMode.Skip && parts.Length > (c.ChangeGroups ? 2 : 1))
            {
                var rawTag = parts[^1].Trim().TrimStart('#');

                if (vocabSet.Count > 0)
                {
                    // 词表里有就用，没有就找最接近的
                    if (vocabSet.Contains(rawTag)) tag = rawTag;
                    else if (!string.IsNullOrEmpty(rawTag))
                    {
                        var best = vocabList.FirstOrDefault(v => rawTag.Contains(v.Name));
                        if (best is not null) tag = best.Name;
                    }
                }
                else tag = rawTag;
            }

            // 都不改的条目跳过
            if (!groupChanged && string.IsNullOrEmpty(tag)) continue;

            list.Add(new OrganizeItem
            {
                ClipId = id,
                Preview = clip.Preview ?? "",
                GroupPath = path,
                Tag = tag,
                Selected = path != "00 待清理"
            });
        }

        return list;
    }

    /// <summary>解析 id|编号|标签，映射回分组路径。</summary>
    private static List<OrganizeItem> ParseByIndex(
        string raw, IReadOnlyList<ClipItem> batch, TaxonomyScheme scheme)
    {
        var list = new List<OrganizeItem>();
        var byId = batch.ToDictionary(x => x.Id, x => x);
        var byIndex = scheme.Groups.Where(g => g.Enabled).ToDictionary(g => g.Index, g => g.Path);

        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();

            // 容错：去掉常见的 markdown / 列表前缀
            if (t.StartsWith("```")) continue;
            if (t.StartsWith("- ")) t = t[2..].Trim();
            if (t.StartsWith("* ")) t = t[2..].Trim();
            if (t.StartsWith("|")) t = t.TrimStart('|').Trim();
            t = t.TrimEnd('|').Trim();

            if (t.Length == 0) continue;

            // 支持 | 和 , 和 tab 分隔
            var parts = t.Split('|');
            if (parts.Length < 2) parts = t.Split(',');
            if (parts.Length < 2) parts = t.Split('\t');
            if (parts.Length < 2) continue;

            if (!long.TryParse(parts[0].Trim(), out var id)) continue;
            if (!byId.TryGetValue(id, out var clip)) continue;
            if (!int.TryParse(parts[1].Trim(), out var idx)) continue;

            var path = idx == 0 ? "00 待清理" : byIndex.GetValueOrDefault(idx, "00 待清理");
            var tag = parts.Length > 2 ? parts[2].Trim().TrimStart('#') : "";

            list.Add(new OrganizeItem
            {
                ClipId = id,
                Preview = clip.Preview ?? "",
                GroupPath = path,
                Tag = tag,
                Selected = idx != 0      // 无效内容默认不勾
            });
        }

        return list;
    }
}
