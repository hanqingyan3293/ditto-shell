using System.IO;
using DittoShell.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace DittoShell.Infrastructure.Sidecar;

/// <summary>
/// 旁路库：标签、来源应用、AI 缓存。
/// 关键约束：建库时必须 PRAGMA encoding='UTF-16le'，否则无法 ATTACH 到 Ditto.db（主库是 UTF-16LE）。
/// </summary>
public sealed class SidecarStore : ITagStore, DittoShell.Domain.Abstractions.ISourceStore, DittoShell.Domain.Abstractions.IOrganizeLogStore, IDisposable
{
    private const int CurrentVersion = 3;

    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _con;

    public SidecarStore(string? dbPath = null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _dbPath = dbPath ?? Path.Combine(dir, "sidecar.db");
    }

    public string DatabasePath => _dbPath;

    // ───────────── 建库与迁移 ─────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var con = await OpenAsync(ct);
            var version = await GetUserVersionAsync(con, ct);

            if (version == 0)
            {
                await CreateSchemaAsync(con, ct);
            }
            else if (version < CurrentVersion)
            {
                await MigrateAsync(con, version, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct) => await OpenAsync(ct);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (_con is { State: System.Data.ConnectionState.Open }) return _con;

        var isNew = !File.Exists(_dbPath);

        _con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString());

        await _con.OpenAsync(ct);

        // 新建库时先设编码（必须在任何建表之前）
        if (isNew)
        {
            using var enc = _con.CreateCommand();
            enc.CommandText = "PRAGMA encoding='UTF-16le';";
            await enc.ExecuteNonQueryAsync(ct);
        }

        using var pragma = _con.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
        await pragma.ExecuteNonQueryAsync(ct);

        return _con;
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection con, CancellationToken ct)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(v ?? 0);
    }

    private static async Task CreateSchemaAsync(SqliteConnection con, CancellationToken ct)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tag (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  name       TEXT NOT NULL UNIQUE,
  color      TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS clip_tag (
  clip_id INTEGER NOT NULL,
  tag_id  INTEGER NOT NULL,
  PRIMARY KEY (clip_id, tag_id)
);
CREATE INDEX IF NOT EXISTS idx_clip_tag_tag ON clip_tag(tag_id);

CREATE TABLE IF NOT EXISTS clip_meta (
  clip_id      INTEGER PRIMARY KEY,
  source_app   TEXT,
  source_title TEXT,
  crc          INTEGER,
  seen_at      INTEGER
);
CREATE INDEX IF NOT EXISTS idx_clip_meta_app ON clip_meta(source_app);

CREATE TABLE IF NOT EXISTS ai_cache (
  clip_id   INTEGER PRIMARY KEY,
  summary   TEXT,
  tags_json TEXT,
  updated_at INTEGER
);

CREATE TABLE IF NOT EXISTS organize_log (
  clip_id      INTEGER PRIMARY KEY,
  content_hash TEXT,
  group_name   TEXT,
  tag          TEXT,
  done_at      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS app_meta (
  key   TEXT PRIMARY KEY,
  value TEXT
);

CREATE TABLE IF NOT EXISTS ai_session (
  id             TEXT PRIMARY KEY,
  title          TEXT,
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL,
  total_tokens   INTEGER DEFAULT 0,
  total_cost     REAL DEFAULT 0,
  summary        TEXT,
  is_compressed  INTEGER DEFAULT 0,
  compress_count INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ai_message (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id      TEXT NOT NULL,
  role            TEXT NOT NULL,
  content         TEXT,
  tool_calls_json TEXT,
  tool_call_id    TEXT,
  name            TEXT,
  at              INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_ai_msg_session ON ai_message(session_id, id);

PRAGMA user_version = 3;
";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateAsync(SqliteConnection con, int from, CancellationToken ct)
    {
        if (from < 3)
        {
            using var c3 = con.CreateCommand();
            c3.CommandText = """
                CREATE TABLE IF NOT EXISTS ai_session (
                  id TEXT PRIMARY KEY, title TEXT,
                  created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                  total_tokens INTEGER DEFAULT 0, total_cost REAL DEFAULT 0,
                  summary TEXT, is_compressed INTEGER DEFAULT 0,
                  compress_count INTEGER DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS ai_message (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT,
                  tool_calls_json TEXT, tool_call_id TEXT, name TEXT,
                  at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ai_msg_session ON ai_message(session_id, id);
                PRAGMA user_version = 3;
                """;
            await c3.ExecuteNonQueryAsync(ct);
        }

        // v1 -> v2：新增 organize_log（AI 整理日志）
        if (from < 2)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS organize_log (
  clip_id      INTEGER PRIMARY KEY,
  content_hash TEXT,
  group_name   TEXT,
  tag          TEXT,
  done_at      INTEGER NOT NULL
);
PRAGMA user_version = 2;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ───────────── 连接管理 ─────────────

    private async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var con = await OpenAsync(ct);
            return await work(con);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────── 标签 ─────────────

    public Task<IReadOnlyList<string>> GetTagsAsync(long clipId, CancellationToken ct = default)
    {
        return WithConnectionAsync<IReadOnlyList<string>>(async con =>
        {
            var list = new List<string>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT t.name FROM tag t
JOIN clip_tag ct ON ct.tag_id = t.id
WHERE ct.clip_id = $id
ORDER BY t.name";
            cmd.Parameters.AddWithValue("$id", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            return list;
        }, ct);
    }

    /// <summary>批量取标签，避免列表 N+1 查询。</summary>
    public Task<Dictionary<long, List<string>>> GetTagsForClipsAsync(
        IReadOnlyList<long> clipIds, CancellationToken ct = default)
    {
        if (clipIds.Count == 0)
            return Task.FromResult(new Dictionary<long, List<string>>());

        return WithConnectionAsync(async con =>
        {
            var result = new Dictionary<long, List<string>>();
            var ids = string.Join(",", clipIds.Take(500));

            using var cmd = con.CreateCommand();
            cmd.CommandText = $@"
SELECT ct.clip_id, t.name FROM clip_tag ct
JOIN tag t ON t.id = ct.tag_id
WHERE ct.clip_id IN ({ids})
ORDER BY t.name";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                if (r.IsDBNull(1)) continue;
                if (!result.TryGetValue(id, out var l)) result[id] = l = new List<string>();
                l.Add(r.GetString(1));
            }
            return result;
        }, ct);
    }

    public Task<List<(long ClipId, int Hits)>> GetClipIdsByTagsAsync(
        IReadOnlyList<string> tags, bool strict, CancellationToken ct = default)
    {
        if (tags.Count == 0) return Task.FromResult(new List<(long, int)>());

        return WithConnectionAsync(async con =>
        {
            var list = new List<(long, int)>();
            var ph = string.Join(",", tags.Select((_, i) => "$t" + i));

            using var cmd = con.CreateCommand();
            cmd.CommandText = $@"
SELECT ct.clip_id, COUNT(DISTINCT ct.tag_id) AS hits
FROM clip_tag ct
JOIN tag t ON t.id = ct.tag_id
WHERE t.name IN ({ph})
GROUP BY ct.clip_id
{(strict ? $"HAVING COUNT(DISTINCT ct.tag_id) = {tags.Count}" : "")}
ORDER BY hits DESC, ct.clip_id DESC
LIMIT 500";

            for (int i = 0; i < tags.Count; i++)
                cmd.Parameters.AddWithValue("$t" + i, tags[i]);

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetInt64(0), r.GetInt32(1)));
            return list;
        }, ct);
    }

    /// <summary>所有标签及其使用次数（供标签选择器）。</summary>
    public Task<List<(string Name, int Count)>> GetAllTagsAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            var list = new List<(string, int)>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT t.name, COUNT(ct.clip_id) AS n
FROM tag t LEFT JOIN clip_tag ct ON ct.tag_id = t.id
GROUP BY t.id
ORDER BY n DESC, t.name";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }, ct);
    }

    public async Task AddTagAsync(long clipId, string tag, CancellationToken ct = default)
    {
        tag = (tag ?? "").Trim().TrimStart('#');
        if (tag.Length == 0) return;

        await WithConnectionAsync<int>(async con =>
        {
            var tagId = await EnsureTagAsync(con, tag, ct);

            using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO clip_tag (clip_id, tag_id) VALUES ($c, $t)";
            cmd.Parameters.AddWithValue("$c", clipId);
            cmd.Parameters.AddWithValue("$t", tagId);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task RemoveTagAsync(long clipId, string tag, CancellationToken ct = default)
    {
        tag = (tag ?? "").Trim().TrimStart('#');
        if (tag.Length == 0) return;

        await WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DELETE FROM clip_tag
WHERE clip_id = $c AND tag_id = (SELECT id FROM tag WHERE name = $n)";
            cmd.Parameters.AddWithValue("$c", clipId);
            cmd.Parameters.AddWithValue("$n", tag);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    private static async Task<long> EnsureTagAsync(SqliteConnection con, string name, CancellationToken ct)
    {
        using (var find = con.CreateCommand())
        {
            find.CommandText = "SELECT id FROM tag WHERE name = $n";
            find.Parameters.AddWithValue("$n", name);
            var existing = await find.ExecuteScalarAsync(ct);
            if (existing is not null) return Convert.ToInt64(existing);
        }

        using var ins = con.CreateCommand();
        ins.CommandText = @"
INSERT INTO tag (name, created_at) VALUES ($n, $t);
SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var id = await ins.ExecuteScalarAsync(ct);
        return Convert.ToInt64(id ?? 0L);
    }

    // ───────────── 来源应用 ─────────────

    public async Task RecordSourceAsync(long clipId, string? app, string? title, long crc,
                                        CancellationToken ct = default)
    {
        await WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO clip_meta (clip_id, source_app, source_title, crc, seen_at)
VALUES ($c, $a, $t, $crc, $now)
ON CONFLICT(clip_id) DO UPDATE SET
  source_app = excluded.source_app,
  source_title = excluded.source_title,
  crc = excluded.crc,
  seen_at = excluded.seen_at";
            cmd.Parameters.AddWithValue("$c", clipId);
            cmd.Parameters.AddWithValue("$a", (object?)app ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$crc", crc);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<(string? App, string? Title)?> GetSourceAsync(long clipId, CancellationToken ct = default)
    {
        return WithConnectionAsync<(string?, string?)?>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT source_app, source_title FROM clip_meta WHERE clip_id = $c";
            cmd.Parameters.AddWithValue("$c", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return (r.IsDBNull(0) ? null : r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1));
        }, ct);
    }

    /// <summary>来源应用分布（Top N）。</summary>
    public Task<List<(string App, int Count)>> GetSourceDistributionAsync(int top, CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            var list = new List<(string, int)>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT IFNULL(NULLIF(source_app, ''), '未知') AS app, COUNT(*) AS n
FROM clip_meta
GROUP BY app
ORDER BY n DESC
LIMIT $top";
            cmd.Parameters.AddWithValue("$top", top);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }, ct);
    }

    // ───────────── 一致性维护 ─────────────

    /// <summary>清理指向已不存在条目的标签/来源记录。返回清理条数。</summary>
    public Task<int> ReconcileAsync(long maxClipId, CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            int n = 0;
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DELETE FROM clip_tag WHERE clip_id > $max;
DELETE FROM clip_meta WHERE clip_id > $max;";
            // 只做 maxClipId 之外的清理；具体条目是否存在由调用方用 Ditto.db 校验后传入删除集合
            cmd.Parameters.AddWithValue("$max", maxClipId);
            n += await cmd.ExecuteNonQueryAsync(ct);
            return n;
        }, ct);
    }

    /// <summary>按给定的失效 clipId 列表清理（由调用方用 Ditto.db 比对得出）。</summary>
    public Task<int> PurgeOrphansAsync(IReadOnlyList<long> orphanClipIds, CancellationToken ct = default)
    {
        if (orphanClipIds.Count == 0) return Task.FromResult(0);

        return WithConnectionAsync(async con =>
        {
            var ids = string.Join(",", orphanClipIds.Take(2000));
            using var cmd = con.CreateCommand();
            cmd.CommandText = $@"
DELETE FROM clip_tag WHERE clip_id IN ({ids});
DELETE FROM clip_meta WHERE clip_id IN ({ids});
DELETE FROM ai_cache WHERE clip_id IN ({ids});";
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>所有被标记过的 clip_id（标签或来源记录）。用于孤儿对账。</summary>
    public Task<IReadOnlyList<long>> GetTaggedClipIdsAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync<IReadOnlyList<long>>(async con =>
        {
            var set = new HashSet<long>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT clip_id FROM clip_tag
UNION
SELECT DISTINCT clip_id FROM clip_meta";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) set.Add(r.GetInt64(0));
            return set.ToList();
        }, ct);
    }

    public Task<int> GetTaggedClipCountAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT clip_id) FROM clip_tag";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(v ?? 0);
        }, ct);
    }

    // ───────── 整理日志 ─────────

    public Task<HashSet<long>> GetOrganizedIdsAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            var set = new HashSet<long>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT clip_id FROM organize_log";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) set.Add(r.GetInt64(0));
            return set;
        }, ct);
    }

    public async Task MarkOrganizedAsync(long clipId, string contentHash, string groupName, string tag,
                                          CancellationToken ct = default)
    {
        await WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO organize_log (clip_id, content_hash, group_name, tag, done_at)
VALUES ($c, $h, $g, $t, $now)
ON CONFLICT(clip_id) DO UPDATE SET
  content_hash = excluded.content_hash,
  group_name = excluded.group_name,
  tag = excluded.tag,
  done_at = excluded.done_at";
            cmd.Parameters.AddWithValue("$c", clipId);
            cmd.Parameters.AddWithValue("$h", contentHash ?? "");
            cmd.Parameters.AddWithValue("$g", groupName ?? "");
            cmd.Parameters.AddWithValue("$t", tag ?? "");
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// 改名历史。
    /// organize_log 现在只存最终分组名，无法直接对比"AI 建议 vs 用户改后"。
    /// 这里用启发式：同一条目如果被整理过多次（done_at 更新），
    /// 且 group_name 变化，就认为用户改过名。
    /// </summary>
    public async Task<List<(string From, string To)>> GetRenameHistoryAsync(
        int limit, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async con =>
        {
            var list = new List<(string, string)>();

            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT group_name, COUNT(*) as n
                FROM organize_log
                WHERE group_name IS NOT NULL AND group_name <> ''
                GROUP BY group_name
                ORDER BY n DESC
                LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$l", limit);

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                // 把两级路径的最后一级作为"风格样本"
                var leaf = name.Contains('>') ? name[(name.LastIndexOf('>') + 1)..] : name;
                if (leaf.Length >= 2) list.Add((name, leaf));
            }

            return list;
        }, ct);
    }

    /// <summary>清空全部整理记录。</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM organize_log";
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// 整理过但内容已变的条目（content_hash 与当前不符）。
    /// 实现上比较保守：只返回 organize_log 里 hash 为空的（说明当时没算 hash）。
    /// </summary>
    public Task<List<long>> GetStaleIdsAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            var list = new List<long>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT clip_id FROM organize_log WHERE content_hash IS NULL OR content_hash = ''";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetInt64(0));
            return list;
        }, ct);
    }

    public async Task ClearAsync(long clipId, CancellationToken ct = default)
    {
        await WithConnectionAsync<int>(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM organize_log WHERE clip_id = $c";
            cmd.Parameters.AddWithValue("$c", clipId);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<int> GetOrganizedCountAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM organize_log";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(v ?? 0);
        }, ct);
    }

    public void Dispose()
    {
        _con?.Dispose();
        _con = null;
        _gate.Dispose();
    }
}
