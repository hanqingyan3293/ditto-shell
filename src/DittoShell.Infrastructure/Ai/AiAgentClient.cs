using System.Text;
using System.Text.Json;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Infrastructure.Ai;

// ───────────────────────── 客户端 ─────────────────────────

/// <summary>
/// 支持多轮 + function calling 的客户端。
/// 和 DeepSeekClient 分开，避免影响现有单轮调用。
/// </summary>
public sealed class AiAgentClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private readonly Func<(string BaseUrl, string Model, string Key)> _config;

    public AiAgentClient(Func<(string, string, string)> config) => _config = config;

    /// <summary>发一轮请求（可能返回 tool_calls）。</summary>
    public async Task<AgentTurnResult> ChatAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentTool> tools,
        int maxTokens = 384000,
        float temperature = 0.2f,
        CancellationToken ct = default)
    {
        var (baseUrl, model, key) = _config();
        if (string.IsNullOrWhiteSpace(key))
            return new AgentTurnResult { Success = false, Error = "还没填写 API Key（设置 → AI）" };

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages.Select(ToWire).ToList(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["stream"] = false
            };

            if (tools.Count > 0)
            {
                body["tools"] = tools.Select(t => new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)
                    }
                }).ToList();
                body["tool_choice"] = "auto";
            }

            var url = baseUrl.TrimEnd('/') + "/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var friendly = (int)resp.StatusCode switch
                {
                    401 => "API Key 无效",
                    402 => "余额不足",
                    404 => $"模型「{model}」不存在",
                    429 => "请求太频繁（限流）",
                    _ => $"接口 {(int)resp.StatusCode}：{Truncate(text, 150)}"
                };
                return new AgentTurnResult
                {
                    Success = false, Error = friendly, ElapsedMs = (int)sw.ElapsedMilliseconds
                };
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var msg = choice.GetProperty("message");

            string? content = null;
            if (msg.TryGetProperty("content", out var ce) && ce.ValueKind == JsonValueKind.String)
                content = ce.GetString();

            List<AgentToolCall>? calls = null;
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                calls = new List<AgentToolCall>();
                foreach (var one in tc.EnumerateArray())
                {
                    var fn = one.GetProperty("function");
                    calls.Add(new AgentToolCall
                    {
                        Id = one.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        Name = fn.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                        Arguments = fn.TryGetProperty("arguments", out var aEl)
                            ? aEl.GetString() ?? "{}" : "{}"
                    });
                }
            }

            int pt = 0, ctk = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p1)) pt = p1.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var p2)) ctk = p2.GetInt32();
            }

            var finish = choice.TryGetProperty("finish_reason", out var fr)
                ? fr.GetString() ?? "" : "";

            return new AgentTurnResult
            {
                Success = true,
                Content = content?.Trim(),
                ToolCalls = calls,
                PromptTokens = pt,
                CompletionTokens = ctk,
                FinishReason = finish,
                ElapsedMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return new AgentTurnResult
            {
                Success = false, Error = "请求超时", ElapsedMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AgentTurnResult
            {
                Success = false, Error = ex.Message, ElapsedMs = (int)sw.ElapsedMilliseconds
            };
        }
    }

    private static Dictionary<string, object?> ToWire(AgentMessage m)
    {
        var d = new Dictionary<string, object?> { ["role"] = m.Role };

        if (m.Content is not null) d["content"] = m.Content;

        if (m.ToolCalls is { Count: > 0 })
        {
            d["tool_calls"] = m.ToolCalls.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["arguments"] = c.Arguments
                }
            }).ToList();
        }

        if (m.ToolCallId is not null) d["tool_call_id"] = m.ToolCallId;
        if (m.Name is not null) d["name"] = m.Name;

        // assistant 只有 tool_calls 没有 content 时，OpenAI 要求 content 为 null 而非缺省
        if (m.Role == "assistant" && m.Content is null) d["content"] = null;

        return d;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
