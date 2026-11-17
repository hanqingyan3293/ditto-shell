using System.Text.Json;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace DittoShell.Infrastructure.Sidecar;

/// <summary>AI 会话的持久化（ai_session / ai_message 两张表）。</summary>
public sealed class AiSessionStore
{
    private readonly SidecarStore _store;

    public AiSessionStore(SidecarStore store) => _store = store;

    /// <summary>
    /// 拿连接。注意：这是 SidecarStore 的共享连接，
    /// 绝对不能 Dispose，否则后续所有读写都会失败。
    /// </summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await _store.InitializeAsync(ct);
        return await _store.OpenConnectionAsync(ct);
    }

    /// <summary>保存或更新一个会话（含全部消息）。</summary>
    public async Task SaveAsync(AgentSession session, CancellationToken ct = default)
    {
        var con = await OpenAsync(ct);

        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ai_session (id, title, created_at, updated_at,
                                        total_tokens, total_cost, summary,
                                        is_compressed, compress_count)
                VALUES ($id, $t, $c, $u, $tok, $cost, $sum, $comp, $cc)
                ON CONFLICT(id) DO UPDATE SET
                    title = $t, updated_at = $u,
                    total_tokens = $tok, total_cost = $cost,
                    summary = $sum, is_compressed = $comp, compress_count = $cc
                """;
            cmd.Parameters.AddWithValue("$id", session.Id);
            cmd.Parameters.AddWithValue("$t", session.Title);
            cmd.Parameters.AddWithValue("$c", session.CreatedAt.Ticks);
            cmd.Parameters.AddWithValue("$u", session.UpdatedAt.Ticks);
            cmd.Parameters.AddWithValue("$tok", session.TotalTokens);
            cmd.Parameters.AddWithValue("$cost", session.TotalCost);
            cmd.Parameters.AddWithValue("$sum", (object?)session.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$comp", session.IsCompressed ? 1 : 0);
            cmd.Parameters.AddWithValue("$cc", session.CompressCount);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 消息：全删全插（简单可靠，会话不会太大）
        await using (var del = con.CreateCommand())
        {
            del.CommandText = "DELETE FROM ai_message WHERE session_id = $id";
            del.Parameters.AddWithValue("$id", session.Id);
            await del.ExecuteNonQueryAsync(ct);
        }

        // 事件也存（role = 'event'），轨迹页可恢复
        foreach (var e in session.Events)
        {
            await using var ev = con.CreateCommand();
            ev.CommandText = @"INSERT INTO ai_message (session_id, role, content, tool_calls_json,
                                        tool_call_id, name, at)
                VALUES ($es, 'event', $ec, NULL, NULL, $en, $eat)";
            ev.Parameters.AddWithValue("$es", session.Id);
            ev.Parameters.AddWithValue("$ec", e.Kind + "|" + e.Text);
            ev.Parameters.AddWithValue("$en", e.Round.ToString());
            ev.Parameters.AddWithValue("$eat", DateTime.Now.Ticks);
            await ev.ExecuteNonQueryAsync(ct);
        }

        foreach (var m in session.Messages)
        {
            await using var ins = con.CreateCommand();
            ins.CommandText = """
                INSERT INTO ai_message (session_id, role, content, tool_calls_json,
                                        tool_call_id, name, at)
                VALUES ($s, $r, $c, $tc, $tcid, $n, $at)
                """;
            ins.Parameters.AddWithValue("$s", session.Id);
            ins.Parameters.AddWithValue("$r", m.Role);
            ins.Parameters.AddWithValue("$c", (object?)m.Content ?? DBNull.Value);
            ins.Parameters.AddWithValue("$tc",
                m.ToolCalls is { Count: > 0 }
                    ? JsonSerializer.Serialize(m.ToolCalls)
                    : DBNull.Value);
            ins.Parameters.AddWithValue("$tcid", (object?)m.ToolCallId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$n", (object?)m.Name ?? DBNull.Value);
            ins.Parameters.AddWithValue("$at", DateTime.Now.Ticks);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>列出最近会话（不含消息）。</summary>
    public async Task<List<(string Id, string Title, DateTime UpdatedAt, int Tokens, double Cost)>>
        ListSessionsAsync(int limit = 30, CancellationToken ct = default)
    {
        var list = new List<(string, string, DateTime, int, double)>();
        var con = await OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, updated_at, total_tokens, total_cost
            FROM ai_session ORDER BY updated_at DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", limit);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add((r.GetString(0),
                      r.IsDBNull(1) ? "未命名" : r.GetString(1),
                      new DateTime(r.GetInt64(2)),
                      r.GetInt32(3),
                      r.GetDouble(4)));
        }
        return list;
    }

    /// <summary>载入一个会话（含消息）。</summary>
    public async Task<AgentSession?> LoadAsync(string id, CancellationToken ct = default)
    {
        var con = await OpenAsync(ct);

        AgentSession? s = null;

        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM ai_session WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                s = new AgentSession
                {
                    Id = r.GetString(r.GetOrdinal("id")),
                    Title = r.IsDBNull(r.GetOrdinal("title")) ? "" : r.GetString(r.GetOrdinal("title")),
                    CreatedAt = new DateTime(r.GetInt64(r.GetOrdinal("created_at"))),
                    UpdatedAt = new DateTime(r.GetInt64(r.GetOrdinal("updated_at"))),
                    TotalTokens = r.GetInt32(r.GetOrdinal("total_tokens")),
                    TotalCost = r.GetDouble(r.GetOrdinal("total_cost")),
                    Summary = r.IsDBNull(r.GetOrdinal("summary")) ? null : r.GetString(r.GetOrdinal("summary")),
                    IsCompressed = r.GetInt32(r.GetOrdinal("is_compressed")) == 1,
                    CompressCount = r.GetInt32(r.GetOrdinal("compress_count"))
                };
            }
        }

        if (s is null) return null;

        await using (var mcmd = con.CreateCommand())
        {
            mcmd.CommandText = "SELECT * FROM ai_message WHERE session_id = $id ORDER BY id";
            mcmd.Parameters.AddWithValue("$id", id);

            await using var r = await mcmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var msg = new AgentMessage
                {
                    Role = r.GetString(r.GetOrdinal("role")),
                    Content = r.IsDBNull(r.GetOrdinal("content")) ? null : r.GetString(r.GetOrdinal("content")),
                    ToolCallId = r.IsDBNull(r.GetOrdinal("tool_call_id")) ? null : r.GetString(r.GetOrdinal("tool_call_id")),
                    Name = r.IsDBNull(r.GetOrdinal("name")) ? null : r.GetString(r.GetOrdinal("name"))
                };

                var tcOrd = r.GetOrdinal("tool_calls_json");
                if (!r.IsDBNull(tcOrd))
                {
                    try
                    {
                        msg.ToolCalls = JsonSerializer.Deserialize<List<AgentToolCall>>(r.GetString(tcOrd));
                    }
                    catch { }
                }

                if (msg.Role == "event")
                {
                    var parts = (msg.Content ?? "").Split('|', 2);
                    s.Events.Add(new AgentEventRecord
                    {
                        Kind = parts.Length > 0 ? parts[0] : "",
                        Text = parts.Length > 1 ? parts[1] : "",
                        Round = int.TryParse(msg.Name, out var r2) ? r2 : 0
                    });
                    continue;
                }

                s.Messages.Add(msg);
            }
        }

        return s;
    }

    /// <summary>删除一个会话。</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var con = await OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_message WHERE session_id = $id; DELETE FROM ai_session WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
