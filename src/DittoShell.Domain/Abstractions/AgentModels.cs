using System.Text.Json;

namespace DittoShell.Domain.Abstractions;

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
