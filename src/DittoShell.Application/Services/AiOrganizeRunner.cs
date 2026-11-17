using System.Collections.Concurrent;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>整理进度。</summary>
public sealed class OrganizeProgress
{
    public int Batch { get; set; }
    public int TotalBatches { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int Concurrency { get; set; } = 4;
    public string Phase { get; set; } = "";
    public double ElapsedSec { get; set; }
    public int CostTenths { get; set; }    // 0.1 分粒度

    public string ProgressText => TotalBatches > 0
        ? $"{Done} / {Total}" : $"{Done} 条";

    public double Ratio => TotalBatches > 0
        ? (double)Batch / TotalBatches : 0;

    public string StatsText =>
        $"成功 {Success} 批 · 失败 {Failed} · 并发 {Concurrency} · 平均 {(Batch > 0 ? ElapsedSec / Batch : 0):F1} 秒/批";
}

/// <summary>整理模式。</summary>
public enum OrganizeMode
{
    /// <summary>只处理新增（跳过 organize_log 里已有的）。</summary>
    Incremental,
    /// <summary>重跑所有（先清空 organize_log）。</summary>
    Full,
    /// <summary>只重试之前失败的。</summary>
    RetryFailed
}

/// <summary>
/// 批量 AI 整理：自适应并发 + 三模式 + 进度回调。
/// </summary>
public sealed class AiOrganizeRunner
{
    private readonly IClipReader _reader;
    private readonly AiService _ai;
    private readonly OrganizeService _organize;

    /// <summary>每批条数（40-100，AI 输出稳定区间）。</summary>
    public int BatchSize { get; set; } = 60;

    /// <summary>初始并发。</summary>
    public int InitialConcurrency { get; set; } = 4;

    /// <summary>最大并发。</summary>
    public int MaxConcurrency { get; set; } = 8;

    public AiOrganizeRunner(IClipReader reader, AiService ai, OrganizeService organize)
    {
        _reader = reader;
        _ai = ai;
        _organize = organize;
    }

    /// <summary>跑一次完整整理。</summary>
    public async Task<List<OrganizeItem>> RunAsync(
        OrganizeMode mode,
        string? userPrompt,
        Action<OrganizeProgress>? onProgress,
        CancellationToken ct = default)
    {
        var progress = new OrganizeProgress { Phase = "读取未归类…" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        onProgress?.Invoke(progress);

        // 全量模式：先清空记录
        if (mode == OrganizeMode.Full)
            await _organize.ClearLogAsync(ct);

        // 取所有未归类
        var clips = (await _reader.SearchAsync("", -1, null)).ToList();

        // 按模式挑候选
        List<ClipItem> candidates;
        if (mode == OrganizeMode.RetryFailed)
        {
            var stale = await _organize.GetStaleIdsAsync(ct);
            var set = stale.ToHashSet();
            candidates = clips.Where(x => set.Contains(x.Id)).ToList();

            // 没有 stale 时退回增量
            if (candidates.Count == 0)
            {
                var (list, _) = await _organize.FilterNewAsync(clips, ct);
                candidates = list.ToList();
            }
        }
        else
        {
            var (list, _) = await _organize.FilterNewAsync(clips, ct);
            candidates = list.ToList();
        }

        progress.Total = candidates.Count;
        if (candidates.Count == 0)
        {
            progress.Phase = "没有需要整理的条目";
            return new List<OrganizeItem>();
        }

        // 分组名（所有批次共用）
        var tree = await _reader.GetGroupTreeAsync();
        var groupNames = new List<string>();
        void Collect(IEnumerable<GroupNode> ns)
        {
            foreach (var n in ns) { groupNames.Add(n.Name); Collect(n.Children); }
        }
        Collect(tree);

        // 切批
        var batches = new List<List<ClipItem>>();
        for (int i = 0; i < candidates.Count; i += BatchSize)
            batches.Add(candidates.Skip(i).Take(BatchSize).ToList());

        progress.TotalBatches = batches.Count;
        progress.Phase = $"共 {batches.Count} 批，开始分析…";
        onProgress?.Invoke(progress);

        var results = new ConcurrentBag<OrganizeItem>();
        int concurrency = InitialConcurrency;

        // 局部计数器（Interlocked 不能作用于属性）
        int successCount = 0, failCount = 0, batchDone = 0, doneCount = 0, consecutiveFail = 0;

        using var sem = new SemaphoreSlim(concurrency, MaxConcurrency);

        var tasks = batches.Select(async (batch, idx) =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var payload = batch.Select(c => (c.Id, c.Preview)).ToList();
                var r = await _ai.OrganizeAsync(payload, groupNames, userPrompt, ct);

                if (r.Success && !string.IsNullOrWhiteSpace(r.Message))
                {
                    var parsed = _organize.Parse(r.Message, batch);
                    foreach (var p in parsed) results.Add(p);

                    Interlocked.Increment(ref successCount);
                    Interlocked.Exchange(ref consecutiveFail, 0);
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                    var cf = Interlocked.Increment(ref consecutiveFail);

                    // 连续失败 → 降并发
                    if (cf >= 3 && concurrency > 1)
                    {
                        concurrency = Math.Max(1, concurrency / 2);
                    }
                }

                Interlocked.Increment(ref batchDone);
                Interlocked.Add(ref doneCount, batch.Count);

                var sc = Volatile.Read(ref successCount);
                if (sc > 0 && sc % 5 == 0 && concurrency < MaxConcurrency)
                    concurrency = Math.Min(MaxConcurrency, concurrency + 2);

                // 汇总到 progress（在主线程回调前一次性赋值）
                progress.Batch = Volatile.Read(ref batchDone);
                progress.Done = Volatile.Read(ref doneCount);
                progress.Success = sc;
                progress.Failed = Volatile.Read(ref failCount);
                progress.Concurrency = concurrency;
                progress.ElapsedSec = sw.Elapsed.TotalSeconds;
                progress.Phase = $"第 {progress.Batch}/{batches.Count} 批完成";

                lock (progress) { onProgress?.Invoke(progress); }
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            progress.Phase = "已取消";
        }

        sw.Stop();
        progress.ElapsedSec = sw.Elapsed.TotalSeconds;
        progress.Phase = $"完成：{results.Count} 条，用时 {sw.Elapsed.TotalMinutes:F1} 分钟";
        onProgress?.Invoke(progress);

        return results.OrderBy(x => x.ClipId).ToList();
    }
}
