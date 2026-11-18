using System.Text.Json;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>
/// 目标模式：持久化目标 + 自动多轮续跑。
/// 对齐 DSH 的 dsh-goal + dsh-goal-round-driver。
/// </summary>
public sealed class AgentGoalService
{
    private readonly IGoalStore? _store;
    private readonly AiAgentService? _agent;
    private readonly AiTools? _tools;

    /// <summary>默认最多轮次（DSH 是 256，剪贴板场景 30 够用）。</summary>
    public int DefaultMaxRounds { get; set; } = 30;

    /// <summary>每轮最多处理条目数（防止单轮太久）。</summary>
    public int MaxItemsPerRound { get; set; } = 200;

    /// <summary>当前目标（每会话一个）。</summary>
    public AgentGoal? Current { get; private set; }

    // ── 轮次回调状态（由 update_goal 工具写入）──
    private string? _roundAction;
    private string _roundSummary = "";
    private int _roundProcessed;
    private string? _roundReason;

    /// <summary>用于多轮执行的会话（必须由 AI 抽屉注入）。</summary>
    public AgentSession? Session { get; private set; }

    /// <summary>绑定会话（由 AI 抽屉在创建目标前调用）。</summary>
    public bool AttachSession(AgentSession? session)
    {
        if (session is null) return false;
        Session = session;
        return true;
    }

    /// <summary>是否正在跑。</summary>
    public bool IsRunning { get; private set; }

    public event Action<AgentGoal>? GoalUpdated;
    public event Action<string>? ProgressChanged;

    private CancellationTokenSource? _cts;

    public AgentGoalService(IGoalStore? store, AiAgentService? agent, AiTools? tools)
    {
        _store = store;
        _agent = agent;
        _tools = tools;
    }

    // ═══════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════

    /// <summary>创建目标。</summary>
    public async Task<AgentGoal> CreateAsync(
        string objective, int? maxRounds = null, CancellationToken ct = default)
    {
        var goal = new AgentGoal
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Objective = objective.Trim(),
            MaxRounds = Math.Clamp(maxRounds ?? DefaultMaxRounds, 1, 200),
            Round = 0,
            Status = GoalStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        Current = goal;
        if (_store is not null) await _store.SaveAsync(goal, ct);
        GoalUpdated?.Invoke(goal);
        return goal;
    }

    /// <summary>AI 通过 update_goal 工具报告本轮结果。</summary>
    public Task OnToolUpdateGoalAsync(
        string action, string summary, int processed, string? reason)
    {
        _roundAction = action;
        _roundSummary = summary ?? "";
        _roundProcessed = processed;
        _roundReason = reason;
        return Task.CompletedTask;
    }

    /// <summary>读取上次未完成的目标（重启恢复用）。</summary>
    public async Task<AgentGoal?> LoadLatestAsync(CancellationToken ct = default)
    {
        if (_store is null) return null;
        try { return await _store.LoadLatestAsync(ct); }
        catch { return null; }
    }

    /// <summary>把读出来的目标恢复成当前目标（不触发新的执行）。</summary>
    public async Task RestoreAsync(AgentGoal goal, CancellationToken ct = default)
    {
        Current = goal;
        if (_store is not null) await _store.SaveAsync(goal, ct);
        GoalUpdated?.Invoke(goal);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        if (Current is null) return;
        Current.Status = GoalStatus.Paused;
        if (_store is not null) await _store.SaveAsync(Current, ct);
        GoalUpdated?.Invoke(Current);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (Current is null || Current.Status == GoalStatus.Completed) return;
        Current.Status = GoalStatus.Active;
        if (_store is not null) await _store.SaveAsync(Current, ct);
        GoalUpdated?.Invoke(Current);
    }

    public async Task CompleteAsync(string? summary = null, CancellationToken ct = default)
    {
        if (Current is null) return;
        Current.Status = GoalStatus.Completed;
        Current.Summary = summary ?? "";
        Current.CompletedAt = DateTime.UtcNow;
        if (_store is not null) await _store.SaveAsync(Current, ct);
        GoalUpdated?.Invoke(Current);
    }

    public async Task BlockAsync(string reason, CancellationToken ct = default)
    {
        if (Current is null) return;
        Current.Status = GoalStatus.Blocked;
        Current.Summary = reason;
        if (_store is not null) await _store.SaveAsync(Current, ct);
        GoalUpdated?.Invoke(Current);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (Current is null) return;
        if (_store is not null) await _store.DeleteAsync(Current.Id, ct);
        Current = null;
        GoalUpdated?.Invoke(new AgentGoal { Status = GoalStatus.Cleared });
    }

    public void Cancel() => _cts?.Cancel();

    // ═══════════════════════════════════════════
    // 自动续跑
    // ═══════════════════════════════════════════

    /// <summary>
    /// 启动目标驱动的多轮执行。
    /// 每轮跑一次 AI，检查目标是否完成，未完成则自动下一轮。
    /// </summary>
    public async Task RunAsync(Action<AgentGoal>? onRound = null, CancellationToken ct = default)
    {
        if (Current is null || _agent is null) return;
        if (IsRunning) return;

        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var consecutiveSameBlock = 0;
        string? lastBlockSignature = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 检查状态
                if (Current.Status == GoalStatus.Paused) break;
                if (Current.Status == GoalStatus.Completed) break;
                if (Current.Round >= Current.MaxRounds)
                {
                    await BlockAsync($"已达最大轮次 {Current.MaxRounds}，未完成", token);
                    break;
                }

                Current.Round++;
                Current.UpdatedAt = DateTime.UtcNow;
                if (_store is not null) await _store.SaveAsync(Current, token);

                ProgressChanged?.Invoke($"第 {Current.Round}/{Current.MaxRounds} 轮");
                GoalUpdated?.Invoke(Current);
                onRound?.Invoke(Current);

                // 跑一轮
                var roundResult = await RunOneRoundAsync(token);

                if (roundResult.Completed)
                {
                    await CompleteAsync(roundResult.Summary, token);
                    break;
                }

                if (roundResult.Blocked is not null)
                {
                    // 连续 3 轮同一阻塞条件才真的 block
                    var sig = roundResult.Blocked;
                    if (sig == lastBlockSignature)
                    {
                        consecutiveSameBlock++;
                    }
                    else
                    {
                        consecutiveSameBlock = 1;
                        lastBlockSignature = sig;
                    }

                    Current.LastBlockReason = sig;

                    if (consecutiveSameBlock >= 3)
                    {
                        await BlockAsync(sig, token);
                        break;
                    }
                }
                else
                {
                    consecutiveSameBlock = 0;
                    lastBlockSignature = null;
                }

                // 短暂间隔，避免打太快
                await Task.Delay(800, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await BlockAsync("执行异常：" + ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            GoalUpdated?.Invoke(Current ?? new AgentGoal());
        }
    }

    /// <summary>跑一轮：给 AI 当前目标 + 进度，让它做一批。</summary>
    private async Task<RoundResult> RunOneRoundAsync(CancellationToken ct)
    {
        if (_agent is null || Current is null) return new RoundResult();

        // 目标模式必须在 AI 抽屉里使用（有会话才能保持上下文）
        if (Session is null)
            return new RoundResult
            {
                Blocked = "目标模式需要在 AI 抽屉里使用。请先打开 AI 抽屉，再创建目标。"
            };

        // 重置本轮状态
        _roundAction = null;
        _roundSummary = "";
        _roundProcessed = 0;
        _roundReason = null;

        var prompt = $"""
现在开始第 {Current.Round} 轮。

目标：{Current.Objective}
进度：已处理 {Current.ProcessedItems} 条，还剩 {Math.Max(0, Current.MaxRounds - Current.Round)} 轮
上轮：{(string.IsNullOrEmpty(Current.LastRoundSummary) ? "（这是第一轮）" : Current.LastRoundSummary)}

这一轮最多处理 {MaxItemsPerRound} 条，做完就停。

**这一轮结束时，你必须调用 update_goal 工具报告结果**：
- 目标已达成 → action = "complete"
- 还需要下一轮 → action = "continue"
- 做不下去 → action = "blocked"（reason 写清楚原因）

如果你不调用 update_goal，系统会认为目标还没完成，继续下一轮。
""";

        try
        {
            var result = await _agent.RunAsync(Session, prompt, null, ct);

            // 更新进度
            Current.ProcessedItems += _roundProcessed;
            if (!string.IsNullOrEmpty(_roundSummary))
                Current.LastRoundSummary = _roundSummary;

            // 按工具报告的动作决定
            return _roundAction switch
            {
                "complete" => new RoundResult { Completed = true, Summary = _roundSummary },
                "blocked" => new RoundResult { Blocked = _roundReason ?? "未说明原因" },
                "continue" => new RoundResult(),
                _ => new RoundResult()   // 没调工具 → 当作 continue
            };
        }
        catch (Exception ex)
        {
            return new RoundResult { Blocked = "AI 调用失败：" + ex.Message };
        }
    }

    private static string ExtractSummary(string text)
    {
        var i = text.LastIndexOf("GOAL_COMPLETE:", StringComparison.Ordinal);
        if (i < 0) i = text.LastIndexOf("GOAL_CONTINUE:", StringComparison.Ordinal);
        if (i < 0) return text.Length > 200 ? text[^200..] : text;
        var line = text[(i + 14)..].Split('\n')[0].Trim();
        return line;
    }

    private static string ExtractAfter(string text, string marker)
    {
        var i = text.LastIndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? "" : text[(i + marker.Length)..].Split('\n')[0].Trim();
    }

    /// <summary>从 AI 输出里抠「处理了 N 条」。</summary>
    private static int ExtractProcessedCount(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"(?:处理|整理|修改|移动)了?\s*(\d+)\s*条");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private sealed class RoundResult
    {
        public bool Completed { get; init; }
        public string? Summary { get; init; }
        public string? Blocked { get; init; }
    }
}
