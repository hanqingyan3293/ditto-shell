using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Infrastructure.Ai;

/// <summary>
/// OpenAI 兼容的聊天客户端。默认指向 DeepSeek。
/// base_url / model / key 全部由调用方提供，便于换厂商。
/// </summary>
public sealed class DeepSeekClient : IAiProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly Func<(string BaseUrl, string Model, string? Key)> _config;

    public DeepSeekClient(Func<(string, string, string?)> config) => _config = config;

    public string Name => "DeepSeek";

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var (baseUrl, model, key) = _config();
        if (string.IsNullOrWhiteSpace(key))
            return new AiResult(false, null, "还没填写 API Key（设置 → AI）", 0);

        var sw = Stopwatch.StartNew();

        try
        {
            var url = baseUrl.TrimEnd('/') + "/chat/completions";
            var body = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = request.SystemPrompt },
                    new { role = "user", content = request.UserContent }
                },
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                stream = false
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var friendly = (int)resp.StatusCode switch
                {
                    401 => "API Key 无效，请检查设置",
                    402 => "账户余额不足",
                    404 => $"模型「{model}」不存在，请检查设置",
                    429 => "请求过于频繁，请稍后再试",
                    _ => $"接口返回 {(int)resp.StatusCode}：{Truncate(text, 120)}"
                };
                return new AiResult(false, null, friendly, (int)sw.ElapsedMilliseconds);
            }

            var parsed = JsonSerializer.Deserialize<ChatResponse>(text);
            var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(content))
                return new AiResult(false, null, "AI 没有返回内容", (int)sw.ElapsedMilliseconds);

            return new AiResult(true, content.Trim(), null, (int)sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return new AiResult(false, null, "请求超时（30 秒）", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new AiResult(false, null, $"网络错误：{ex.Message}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AiResult(false, null, $"调用失败：{ex.Message}", (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<(bool Ok, string Message, int ElapsedMs)> TestAsync(CancellationToken ct = default)
    {
        var (baseUrl, model, key) = _config();
        if (string.IsNullOrWhiteSpace(key)) return (false, "还没填写 API Key", 0);

        var sw = Stopwatch.StartNew();
        var r = await CompleteAsync(new AiRequest("你是一个测试助手。", "回复两个字：可用", 16, 0f), ct);
        sw.Stop();

        return r.Success
            ? (true, $"连接正常 · 模型 {model} · {r.ElapsedMs} ms", r.ElapsedMs)
            : (false, r.Error ?? "连接失败", r.ElapsedMs);
    }

    public async Task<(bool Ok, List<string> Models, string? Error)> ListModelsAsync(CancellationToken ct = default)
    {
        var (baseUrl, _, key) = _config();
        if (string.IsNullOrWhiteSpace(key))
            return (false, new List<string>(), "还没填写 API Key");

        try
        {
            var url = baseUrl.TrimEnd('/') + "/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return (false, new List<string>(), $"接口返回 {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(text);
            var models = new List<string>();

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                    }
                }
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return (true, models, null);
        }
        catch (Exception ex)
        {
            return (false, new List<string>(), ex.Message);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ───────── 响应模型 ─────────

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public Msg? Message { get; set; }
    }

    private sealed class Msg
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
