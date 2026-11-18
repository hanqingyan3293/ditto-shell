

using System.Text.Json;

namespace DittoShell.Domain.Abstractions;
// ───────────────────────── 权限 ─────────────────────────

/// <summary>AI 权限等级。</summary>
public enum AiPermissionLevel
{
    /// <summary>只读：只能查，不能改。</summary>
    ReadOnly = 0,

    /// <summary>安全：能改，但每次弹确认（默认）。</summary>
    Safe = 1,

    /// <summary>完全：能改，直接执行。</summary>
    FullAccess = 2
}

/// <summary>工具的危险等级。</summary>
public enum ToolRisk
{
    /// <summary>只读，任何模式都允许。</summary>
    ReadOnly = 0,

    /// <summary>改数据但可逆（加标签、标记隐私、导出）。</summary>
    Write = 1,

    /// <summary>改数据且影响大（移动分组）。</summary>
    Dangerous = 2
}


// ───────────────────────── 数据模型 ─────────────────────────

/// <summary>会话里的一条消息（对齐 OpenAI / DeepSeek 格式）。</summary>
public sealed class AgentMessage
{
    public string Role { get; set; } = "";          // system / user / assistant / tool
    public string? Content { get; set; }
    public List<AgentToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }               // tool 消息的工具名

    public static AgentMessage System(string c) => new() { Role = "system", Content = c };
    public static AgentMessage User(string c) => new() { Role = "user", Content = c };
    public static AgentMessage Assistant(string? c, List<AgentToolCall>? calls = null)
        => new() { Role = "assistant", Content = c, ToolCalls = calls };
    public static AgentMessage Tool(string callId, string name, string content)
        => new() { Role = "tool", ToolCallId = callId, Name = name, Content = content };
}

public sealed class AgentToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

/// <summary>一个工具的定义（给 AI 看的 schema）。</summary>
public sealed class AgentTool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string ParametersJson { get; init; } = """{"type":"object","properties":{}}""";
}

/// <summary>一轮请求的结果。</summary>
public sealed class AgentTurnResult
{
    public bool Success { get; init; }
    public string? Content { get; init; }
    public List<AgentToolCall>? ToolCalls { get; init; }
    public string? Error { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int ElapsedMs { get; init; }
    public string FinishReason { get; init; } = "";

    public int TotalTokens => PromptTokens + CompletionTokens;
    public bool WantsTools => ToolCalls is { Count: > 0 };
}

/// <summary>会话（多轮上下文）。</summary>
public sealed class AgentSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<AgentMessage> Messages { get; set; } = new();
    public int TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public string? Summary { get; set; }
    public bool IsCompressed { get; set; }
    public int CompressCount { get; set; }

    /// <summary>事件流（工具调用等），用于轨迹页和会话恢复。</summary>
    public List<AgentEventRecord> Events { get; set; } = new();
}

/// <summary>持久化的事件记录。</summary>
public sealed class AgentEventRecord
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public int Round { get; set; }
    public string At { get; set; } = "";
}

// ───────────────────────── 目标模式 ─────────────────────────

/// <summary>目标状态。</summary>
public enum GoalStatus
{
    /// <summary>进行中。</summary>
    Active = 0,

    /// <summary>已暂停。</summary>
    Paused = 1,

    /// <summary>已完成。</summary>
    Completed = 2,

    /// <summary>受阻（连续多轮同一阻塞原因）。</summary>
    Blocked = 3,

    /// <summary>已清除。</summary>
    Cleared = 4
}

/// <summary>一个长期完成目标。</summary>
public sealed class AgentGoal
{
    public string Id { get; set; } = "";
    public string Objective { get; set; } = "";
    public int Round { get; set; }
    public int MaxRounds { get; set; } = 30;
    public int ProcessedItems { get; set; }
    public GoalStatus Status { get; set; } = GoalStatus.Active;
    public string LastRoundSummary { get; set; } = "";
    public string LastBlockReason { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public double Progress => MaxRounds > 0 ? (double)Round / MaxRounds : 0;
}

/// <summary>目标存储接口。</summary>
public interface IGoalStore
{
    Task SaveAsync(AgentGoal goal, CancellationToken ct = default);
    Task<AgentGoal?> LoadAsync(string id, CancellationToken ct = default);
    Task<AgentGoal?> LoadLatestAsync(CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
