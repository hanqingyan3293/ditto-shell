using DittoShell.Domain.Abstractions;

namespace DittoShell.Infrastructure.Sidecar;

/// <summary>目标的 sidecar 持久化。</summary>
public sealed class GoalStore : IGoalStore
{
    private readonly SidecarStore _store;

    public GoalStore(SidecarStore store) => _store = store;

    public async Task SaveAsync(AgentGoal goal, CancellationToken ct = default)
    {
        await _store.WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_goal
                  (id, objective, round, max_rounds, processed_items, status,
                   last_summary, block_reason, summary, created_at, updated_at, completed_at)
                VALUES ($id, $obj, $r, $mr, $pi, $st, $ls, $br, $sm, $ca, $ua, $cmp)
                ON CONFLICT(id) DO UPDATE SET
                  objective = excluded.objective,
                  round = excluded.round,
                  max_rounds = excluded.max_rounds,
                  processed_items = excluded.processed_items,
                  status = excluded.status,
                  last_summary = excluded.last_summary,
                  block_reason = excluded.block_reason,
                  summary = excluded.summary,
                  updated_at = excluded.updated_at,
                  completed_at = excluded.completed_at
                """;

            cmd.Parameters.AddWithValue("$id", goal.Id);
            cmd.Parameters.AddWithValue("$obj", goal.Objective);
            cmd.Parameters.AddWithValue("$r", goal.Round);
            cmd.Parameters.AddWithValue("$mr", goal.MaxRounds);
            cmd.Parameters.AddWithValue("$pi", goal.ProcessedItems);
            cmd.Parameters.AddWithValue("$st", (int)goal.Status);
            cmd.Parameters.AddWithValue("$ls", goal.LastRoundSummary ?? "");
            cmd.Parameters.AddWithValue("$br", goal.LastBlockReason ?? "");
            cmd.Parameters.AddWithValue("$sm", goal.Summary ?? "");
            cmd.Parameters.AddWithValue("$ca", goal.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", goal.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$cmp", goal.CompletedAt?.ToString("O") ?? (object)DBNull.Value);

            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<AgentGoal?> LoadAsync(string id, CancellationToken ct = default)
    {
        return await _store.WithConnectionAsync<AgentGoal?>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM ai_goal WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);

            using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Map(r) : null;
        }, ct);
    }

    public async Task<AgentGoal?> LoadLatestAsync(CancellationToken ct = default)
    {
        return await _store.WithConnectionAsync<AgentGoal?>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM ai_goal
                WHERE status IN (0, 1)     -- Active / Paused
                ORDER BY updated_at DESC
                LIMIT 1
                """;

            using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Map(r) : null;
        }, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _store.WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM ai_goal WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    private static AgentGoal Map(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        static int Idx(Microsoft.Data.Sqlite.SqliteDataReader r, string col)
            => r.GetOrdinal(col);

        return new AgentGoal
        {
            Id = r.GetString(Idx(r, "id")),
            Objective = r.GetString(Idx(r, "objective")),
            Round = r.GetInt32(Idx(r, "round")),
            MaxRounds = r.GetInt32(Idx(r, "max_rounds")),
            ProcessedItems = r.GetInt32(Idx(r, "processed_items")),
            Status = (GoalStatus)r.GetInt32(Idx(r, "status")),
            LastRoundSummary = r.GetString(Idx(r, "last_summary")),
            LastBlockReason = r.GetString(Idx(r, "block_reason")),
            Summary = r.GetString(Idx(r, "summary")),
            CreatedAt = DateTime.TryParse(r.GetString(Idx(r, "created_at")), out var ca)
                ? ca : DateTime.UtcNow,
            UpdatedAt = DateTime.TryParse(r.GetString(Idx(r, "updated_at")), out var ua)
                ? ua : DateTime.UtcNow,
            CompletedAt = r.IsDBNull(Idx(r, "completed_at")) ? null
                : DateTime.TryParse(r.GetString(Idx(r, "completed_at")), out var cp) ? cp : null
        };
    }
}
