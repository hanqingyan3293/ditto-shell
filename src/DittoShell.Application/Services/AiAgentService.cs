using System.Text;
using System.Text.Json;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>Agent 运行过程中的一个事件（给 UI 显示进度）。</summary>
public sealed record AgentEvent(
    string Kind,            // thinking / tool_start / tool_done / answer / error / compressed
    string Text,
    int Round = 0,
    int Tokens = 0,
    double Cost = 0);

/// <summary>一轮里的一个 step（一次模型调用 + 工具执行）。</summary>
public sealed class StepOutcome
{
    public int StepIndex { get; init; }
    public string? AssistantText { get; init; }
    public int ToolCallCount { get; init; }
    public int Tokens { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

/// <summary>一次用户提问（turn）的完整结果。</summary>
public sealed class TurnOutcome
{
    public int TurnIndex { get; init; }
    public List<StepOutcome> Steps { get; init; } = new();
    public string Answer { get; init; } = "";
    public int TotalTokens { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>一次 Agent 运行的最终结果。</summary>
public sealed class AgentResult
{
    public bool Success { get; init; }
    public string Answer { get; init; } = "";
    public List<long> ResultIds { get; init; } = new();
    public List<AgentEvent> Events { get; init; } = new();
    public int TotalTokens { get; init; }
    public double Cost { get; init; }
    public int ElapsedMs { get; init; }
    public string? Error { get; init; }
    public bool Compressed { get; init; }

    /// <summary>本次提问的 step 明细。</summary>
    public List<StepOutcome> Steps { get; init; } = new();
}

/// <summary>
/// AI Agent：多轮工具调用 + 上下文压缩。
/// </summary>
public sealed class AiAgentService
{
    private readonly Func<IReadOnlyList<AgentMessage>, IReadOnlyList<AgentTool>, int, float, CancellationToken, Task<AgentTurnResult>> _chat;
    private readonly AiTools _tools;

    /// <summary>最多几轮（防止无限循环）。</summary>
    public int MaxRounds { get; set; } = 8;

    /// <summary>上下文预算（tokens）。超过 90% 触发压缩。</summary>
    public int ContextBudget { get; set; } = 524_288;

    /// <summary>压缩后保留多少 tokens 的原始对话。</summary>
    public int KeepAfterCompress { get; set; } = 16_000;

    /// <summary>输入价格（元/百万 tokens，缓存未命中）。</summary>
    public double InputPrice { get; set; } = 1.0;

    /// <summary>输出价格（元/百万 tokens）。</summary>
    public double OutputPrice { get; set; } = 4.0;

    public AiAgentService(
        Func<IReadOnlyList<AgentMessage>, IReadOnlyList<AgentTool>, int, float, CancellationToken, Task<AgentTurnResult>> chat,
        AiTools tools)
    {
        _chat = chat;
        _tools = tools;
    }

    private const string SystemPrompt = """
你是 Ditto 剪贴板助手。用户会问关于他剪贴板历史的问题。

规则：
1. 先调 get_context 了解今天日期和总量，再决定怎么查。
2. 时间类问题（上周/昨天/最近）必须自己换算成具体日期再调 filter。
3. 可以用多个工具组合查询。每轮最多调 3 个。
4. 最多 6 轮。拿到足够信息就回答。
5. 回答用中文，简短，列出找到的条目用 #编号 标注。
6. 不要编造不存在的条目。找不到就直说。
7. 写操作（移分组/加标签/标隐私/导出）会请求用户确认，你只需说明意图。
""";

    /// <summary>跑一次完整会话轮次（用户提一个问题）。</summary>
    public async Task<AgentResult> RunAsync(
        AgentSession session,
        string userInput,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var events = new List<AgentEvent>();

        void Emit(AgentEvent e)
        {
            events.Add(e);

            try
            {
                session.Events.Add(new AgentEventRecord
                {
                    Kind = e.Kind,
                    Text = e.Text,
                    Round = e.Round,
                    At = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch { }

            try { onEvent?.Invoke(e); } catch { }
        }

        // 1) 首次进入：初始化 system + 工具
        if (session.Messages.Count == 0)
        {
            session.Messages.Add(AgentMessage.System(SystemPrompt));
            session.Title = userInput.Length > 24 ? userInput[..24] + "…" : userInput;
        }

        // 2) 加用户消息
        session.Messages.Add(AgentMessage.User(userInput));
        session.UpdatedAt = DateTime.Now;

        // 3) 预算检查 + 压缩
        if (EstimateTokens(session.Messages) > ContextBudget * 0.9)
        {
            Emit(new AgentEvent("compressed", "上下文接近上限，正在压缩早期对话…"));
            await CompressAsync(session, ct);
        }

        _tools.Reset();

        int totalTokens = 0;
        var steps = new List<StepOutcome>();
        string? finalAnswer = null;
        bool truncatedSticky = false;

        for (int round = 1; round <= MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            // 快用完轮数时提醒 AI 收口
            if (round == MaxRounds - 1)
            {
                session.Messages.Add(AgentMessage.System(
                    "注意：只剩 2 轮。请尽快用已获得的信息回答用户，不要再搜新关键词。"));
            }
            else if (round == MaxRounds)
            {
                session.Messages.Add(AgentMessage.System(
                    "这是最后一轮。不要再调用工具，直接用现有信息回答。"));
            }

            Emit(new AgentEvent("thinking", $"第 {round} 轮：AI 正在思考…", round));

            var turn = await _chat(session.Messages, AiTools.Definitions, 384000, 0.2f, ct);
            totalTokens += turn.TotalTokens;

            // max-tokens 粘性：一旦截断，整轮标记
            var truncated = turn.FinishReason == "length";
            if (truncated) truncatedSticky = true;

            if (!turn.Success)
            {
                // 失败也要把已完成的 step 记下来
                steps.Add(new StepOutcome
                {
                    StepIndex = round,
                    Tokens = turn.TotalTokens,
                    Truncated = truncated,
                    Error = turn.Error
                });

                Emit(new AgentEvent("error", turn.Error ?? "调用失败", round));

                return new AgentResult
                {
                    Success = false,
                    Error = turn.Error,
                    Events = events,
                    TotalTokens = totalTokens,
                    Cost = CalcCost(totalTokens, 0),
                    ElapsedMs = (int)sw.ElapsedMilliseconds,
                    Steps = steps
                };
            }

            // assistant 消息入上下文
            var assistantMsg = AgentMessage.Assistant(turn.Content, turn.ToolCalls);
            session.Messages.Add(assistantMsg);

            // 没调工具 → 这就是答案
            if (!turn.WantsTools)
            {
                finalAnswer = turn.Content ?? "";
                steps.Add(new StepOutcome
                {
                    StepIndex = round,
                    AssistantText = finalAnswer,
                    Tokens = turn.TotalTokens,
                    Truncated = truncated
                });
                Emit(new AgentEvent("answer", finalAnswer, round, turn.TotalTokens));
                break;
            }

            // 执行工具（最多 3 个）
            var calls = turn.ToolCalls!.Take(3).ToList();
            var stepToolCount = 0;
            var toolErrors = new List<string>();

            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();

                Emit(new AgentEvent("tool_start",
                    $"调用 {call.Name}({TrimArgs(call.Arguments)})", round));

                string result;
                try
                {
                    result = await _tools.ExecuteAsync(call.Name, call.Arguments, ct);
                }
                catch (Exception ex)
                {
                    // 工具失败不中断整轮：把错误当结果回给 AI
                    result = "工具出错：" + ex.Message;
                    toolErrors.Add(call.Name);
                }

                // 立即写进会话（失败恢复：保证已完成的都落盘）
                session.Messages.Add(AgentMessage.Tool(call.Id, call.Name, result));
                stepToolCount++;

                var brief = result.Length > 60
                    ? result[..60].Replace('\n', ' ') + "…"
                    : result.Replace('\n', ' ');
                Emit(new AgentEvent("tool_done", $"{call.Name} → {brief}", round));
            }

            steps.Add(new StepOutcome
            {
                StepIndex = round,
                ToolCallCount = stepToolCount,
                Tokens = turn.TotalTokens,
                Truncated = truncated,
                Error = toolErrors.Count > 0 ? string.Join(",", toolErrors) + " 出错" : null
            });
        }

        if (finalAnswer is null)
        {
            finalAnswer = truncatedSticky
                ? "分析完成，但输出被长度限制截断。请把问题说得更具体一些。"
                : "分析完成，但达到最大轮数。请把问题说得更具体一些。";
            Emit(new AgentEvent("answer", finalAnswer, MaxRounds));
        }

        session.TotalTokens += totalTokens;
        var cost = CalcCost(totalTokens, 0);
        session.TotalCost += cost;

        sw.Stop();

        return new AgentResult
        {
            Success = true,
            Answer = finalAnswer,
            ResultIds = _tools.Current.Select(x => x.Id).ToList(),
            Events = events,
            TotalTokens = totalTokens,
            Cost = cost,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            Compressed = session.IsCompressed,
            Steps = steps
        };
    }

    /// <summary>压缩早期对话成摘要。</summary>
    private async Task CompressAsync(AgentSession session, CancellationToken ct)
    {
        // 找到要保留的部分（从后往前累计 token）
        var msgs = session.Messages;
        int keepFrom = msgs.Count;

        if (msgs.Count <= 3) return;

        // 跳过 system（index 0）
        int acc = 0;
        for (int i = msgs.Count - 1; i > 1; i--)
        {
            acc += EstimateOne(msgs[i]);
            if (acc >= KeepAfterCompress) { keepFrom = i; break; }
            if (i == 2) { keepFrom = 2; }
        }

        if (keepFrom <= 2) return;   // 没东西可压缩

        // 要压缩的部分
        var toCompress = msgs.Skip(1).Take(keepFrom - 1).ToList();
        if (toCompress.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var m in toCompress)
        {
            var role = m.Role switch { "user" => "用户", "assistant" => "AI", "tool" => "工具结果", _ => m.Role };
            var text = (m.Content ?? "").Replace('\n', ' ').Trim();
            if (text.Length > 300) text = text[..300] + "…";
            sb.Append(role).Append(": ").AppendLine(text);
        }

        var compressReq = new List<AgentMessage>
        {
            AgentMessage.System("把下面的对话压缩成 600 tokens 以内的摘要。必须保留：用户意图、提到的条目编号、已执行的写操作、未完成的需求。只输出摘要正文。"),
            AgentMessage.User(sb.ToString())
        };

        var r = await _chat(compressReq, Array.Empty<AgentTool>(), 1200, 0.1f, ct);

        if (r.Success && !string.IsNullOrWhiteSpace(r.Content))
        {
            session.Summary = r.Content.Trim();
            session.IsCompressed = true;
            session.CompressCount++;

            // 重建消息：system + 摘要 + 保留的尾部
            var kept = msgs.Skip(keepFrom).ToList();
            msgs.Clear();
            msgs.Add(AgentMessage.System(SystemPrompt));
            msgs.Add(AgentMessage.System("以下是之前对话的摘要：" + Environment.NewLine + session.Summary));
            msgs.AddRange(kept);
        }
    }

    /// <summary>粗估 token（中文按 1.5 字/token，英文按 4 字/token）。</summary>
    public static int EstimateTokens(IEnumerable<AgentMessage> msgs)
    {
        int sum = 0;
        foreach (var m in msgs) sum += EstimateOne(m);
        return sum;
    }

    private static int EstimateOne(AgentMessage m)
    {
        var c = m.Content ?? "";
        int cjk = 0;
        foreach (var ch in c) if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;

        var ascii = c.Length - cjk;
        var t = (int)(cjk / 1.5) + (int)(ascii / 4.0) + 8;

        if (m.ToolCalls is { Count: > 0 })
            t += m.ToolCalls.Sum(x => (x.Name.Length + x.Arguments.Length) / 4 + 10);

        return t;
    }

    private double CalcCost(int tokens, int cachedTokens)
    {
        // 简化：全部按未命中算（保守估计）
        return tokens / 1_000_000.0 * Math.Max(InputPrice, OutputPrice);
    }

    private static string TrimArgs(string json)
    {
        var s = (json ?? "").Replace('\n', ' ').Trim();
        return s.Length > 50 ? s[..50] + "…" : s;
    }
}
