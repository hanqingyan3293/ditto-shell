using System.Collections.Concurrent;
using System.Globalization;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;
using Microsoft.Data.Sqlite;

namespace DittoShell.Infrastructure.DittoDb;

/// <summary>
/// Ditto.db 只读访问。所有查询参数化；连接保持打开，用信号量串行化。
/// 绝不执行任何写操作（连接串 Mode=ReadOnly 从驱动层兜底）。
/// </summary>
public sealed class DittoDbReader : IClipReader, IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _con;

    // 「哪些 clip 带图片」的缓存，首次查询后复用
    private HashSet<long>? _imageIds;
    private DateTime _imageIdsLoadedAt;

    private ITagStore? _tagStore;
    private ISourceStore? _sourceStore;

    /// <summary>由 DI 在构造后注入（避免改构造函数签名）。</summary>
    public void AttachFilters(ITagStore tags, ISourceStore sources)
    {
        _tagStore = tags;
        _sourceStore = sources;
    }

    public DittoDbReader(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string DatabasePath => _dbPath;

    // ───────────────────────── 连接 ─────────────────────────

    private async Task<SqliteConnection> ConnectionAsync(CancellationToken ct)
    {
        if (_con is { State: System.Data.ConnectionState.Open }) return _con;

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 3
        }.ToString();

        _con = new SqliteConnection(cs);
        await _con.OpenAsync(ct);

        using var cmd = _con.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=3000; PRAGMA query_only=ON;";
        await cmd.ExecuteNonQueryAsync(ct);

        return _con;
    }

    private async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var con = await ConnectionAsync(ct);
            return await work(con);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────── 图片 ID 集合 ─────────────────────────

    private async Task<HashSet<long>> GetImageIdsAsync(CancellationToken ct)
    {
        if (_imageIds is not null && (DateTime.UtcNow - _imageIdsLoadedAt).TotalSeconds < 30)
            return _imageIds;

        var set = await WithConnectionAsync(async con =>
        {
            var result = new HashSet<long>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT lParentID FROM Data WHERE strClipBoardFormat='CF_DIB'";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) result.Add(r.GetInt64(0));
            return result;
        }, ct);

        _imageIds = set;
        _imageIdsLoadedAt = DateTime.UtcNow;
        return set;
    }

    // ───────────────────────── 查询 ─────────────────────────

    private const string BaseSelect =
        "SELECT lID, mText, lParentID, lDate, CRC, IFNULL(lastPasteDate, 0) FROM Main WHERE bIsGroup = 0";

    private async Task<List<ClipItem>> ReadItemsAsync(
        SqliteConnection con, string sql, Action<SqliteCommand>? bind, CancellationToken ct)
    {
        var imageIds = _imageIds ?? new HashSet<long>();

        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);

        var list = new List<ClipItem>(128);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            long id = r.GetInt64(0);
            string text = r.IsDBNull(1) ? "" : r.GetString(1);
            long parent = r.IsDBNull(2) ? -1 : r.GetInt64(2);
            long date = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            long crc = r.IsDBNull(4) ? 0 : r.GetInt64(4);

            bool isImage = imageIds.Contains(id);
            var kind = KindClassifierHolder.Classify(text, isImage);

            long lastPaste = 0;
            try { if (!r.IsDBNull(5)) lastPaste = r.GetInt64(5); } catch { }

            list.Add(new ClipItem(
                id,
                text,
                parent,
                DateTimeOffset.FromUnixTimeSeconds(date == 0 ? 0 : date).ToLocalTime(),
                kind,
                text.Length,
                crc,
                lastPaste));
        }
        return list;
    }

    public async Task<IReadOnlyList<ClipItem>> GetLatestAsync(int limit, long? groupId, CancellationToken ct = default)
    {
        await GetImageIdsAsync(ct);

        var sql = BaseSelect;
        if (groupId is > 0) sql += " AND lParentID = $g";
        sql += " ORDER BY clipOrder DESC LIMIT $n";

        return await WithConnectionAsync(con => ReadItemsAsync(con, sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$n", limit);
            if (groupId is > 0) cmd.Parameters.AddWithValue("$g", groupId.Value);
        }, ct), ct);
    }

    public async Task<IReadOnlyList<ClipItem>> SearchAsync(
        string query, long? groupId, ClipKind? kind,
        string? tagFilter = null, string? fromFilter = null,
        CancellationToken ct = default)
    {
        await GetImageIdsAsync(ct);

        var raw = (query ?? "").Trim();

        // ── ID 搜索 ──
        // "#9906"  → 只查 ID
        // "9906"   → 先查 ID，查不到再当文本（3 位以上才尝试）
        long? idQuery = null;
        bool idOnly = false;

        if (raw.StartsWith("#") && raw.Length > 1 && long.TryParse(raw[1..], out var idA))
        {
            idQuery = idA;
            idOnly = true;
        }
        else if (raw.Length >= 3 && raw.Length <= 9 && long.TryParse(raw, out var idB) && idB > 0)
        {
            idQuery = idB;
        }

        if (idQuery is not null)
        {
            var byId = await WithConnectionAsync(con =>
                ReadItemsAsync(con, BaseSelect + " AND lID = $id LIMIT 1",
                    cmd => cmd.Parameters.AddWithValue("$id", idQuery.Value), ct), ct);

            if (byId.Count > 0) return byId;
            if (idOnly) return Array.Empty<ClipItem>();
            // 不是 ID → 继续当文本搜
        }

        var terms = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sql = BaseSelect;
        for (int i = 0; i < terms.Length; i++)
            sql += $" AND mText LIKE $k{i} ESCAPE '\\'";
        if (groupId is > 0) sql += " AND lParentID = $g";
        else if (groupId == -1) sql += " AND lParentID = -1";   // Ditto 的未归类

        // 指定了分组/未归类/类型时，全量取（不限 500）
        var needAll = groupId is not null || kind is not null;
        sql += needAll ? " ORDER BY clipOrder DESC" : " ORDER BY clipOrder DESC LIMIT 500";

        var items = await WithConnectionAsync(con => ReadItemsAsync(con, sql, cmd =>
        {
            for (int i = 0; i < terms.Length; i++)
                cmd.Parameters.AddWithValue($"$k{i}", "%" + EscapeLike(terms[i]) + "%");
            if (groupId is > 0) cmd.Parameters.AddWithValue("$g", groupId.Value);
        }, ct), ct);

        // 按类型过滤（用全库索引，保证和侧栏数字一致）
        if (kind is not null)
        {
            var index = await EnsureKindIndexAsync(ct);
            var ids = index.GetValueOrDefault(kind.Value);
            items = ids is null ? new List<ClipItem>() : items.Where(x => ids.Contains(x.Id)).ToList();
        }

        // 按标签过滤（chip: tag:xxx，多个空格分隔 = AND）
        if (!string.IsNullOrWhiteSpace(tagFilter))
        {
            var store = _tagStore;
            if (store is not null)
            {
                var tags = tagFilter.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var hits = await store.GetClipIdsByTagsAsync(tags, strict: tags.Count > 1);
                var idSet = hits.Select(h => h.ClipId).ToHashSet();
                items = items.Where(x => idSet.Contains(x.Id)).ToList();
            }
        }

        // 按来源程序过滤（chip: from:chrome）
        if (!string.IsNullOrWhiteSpace(fromFilter) && _sourceStore is not null)
        {
            var key = fromFilter.Trim();
            var keep = new List<ClipItem>();

            foreach (var it in items)
            {
                var src = await _sourceStore.GetSourceAsync(it.Id, ct);
                if (src is { } s2 && s2.App is not null
                    && s2.App.Contains(key, StringComparison.OrdinalIgnoreCase))
                    keep.Add(it);
            }

            items = keep;
        }

        return items;
    }

    public async Task<ClipDetail?> GetDetailAsync(long clipId, CancellationToken ct = default)
    {
        var item = await WithConnectionAsync(async con =>
        {
            var sql = BaseSelect + " AND lID = $id LIMIT 1";
            var list = await ReadItemsAsync(con, sql, cmd => cmd.Parameters.AddWithValue("$id", clipId), ct);
            return list.FirstOrDefault();
        }, ct);

        if (item is null) return null;

        var (formats, size) = await WithConnectionAsync(async con =>
        {
            var fs = new List<string>();
            long total = 0;
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT strClipBoardFormat, length(ooData) FROM Data WHERE lParentID = $id";
            cmd.Parameters.AddWithValue("$id", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(0)) fs.Add(r.GetString(0));
                if (!r.IsDBNull(1)) total += r.GetInt64(1);
            }
            return (fs, total);
        }, ct);

        return new ClipDetail(item, item.Preview, formats, size, 0, null);
    }

    public async Task<IReadOnlyList<GroupNode>> GetGroupTreeAsync(CancellationToken ct = default)
    {
        var rows = await WithConnectionAsync(async con =>
        {
            var list = new List<(long Id, string Name, long Parent, int Count)>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT lID, mText, lParentID FROM Main WHERE bIsGroup = 1 ORDER BY clipOrder DESC";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? -1 : r.GetInt64(2),
                    0));
            }
            return list;
        }, ct);

        var counts = await WithGroupCountsAsync(ct);
        var byId = rows.ToDictionary(x => x.Id, x => new GroupNode
        {
            Id = x.Id,
            Name = x.Name,
            ParentId = x.Parent,
            Count = counts.TryGetValue(x.Id, out var c) ? c : 0
        });

        var roots = new List<GroupNode>();
        foreach (var node in byId.Values)
        {
            if (node.ParentId != -1 && byId.TryGetValue(node.ParentId, out var parent))
                parent.Children.Add(node);
            else if (node.ParentId == -1)
                roots.Add(node);
            else
                roots.Add(node);
        }

        SortTree(roots);
        return roots;
    }

    private static void SortTree(List<GroupNode> nodes)
    {
        nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        foreach (var n in nodes) SortTree(n.Children);
    }

    private async Task<Dictionary<long, int>> WithGroupCountsAsync(CancellationToken ct)
    {
        return await WithConnectionAsync(async con =>
        {
            var dict = new Dictionary<long, int>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT lParentID, COUNT(*) FROM Main WHERE bIsGroup = 0 GROUP BY lParentID";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(0)) continue;
                dict[r.GetInt64(0)] = r.GetInt32(1);
            }
            return dict;
        }, ct);
    }

    public async Task<long> GetMaxClipIdAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT IFNULL(MAX(lID), 0) FROM Main";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }, ct);
    }

    public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Main WHERE bIsGroup = 0";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }, ct);
    }

    private IReadOnlyDictionary<string, int>? _kindCountsCache;

    /// <summary>类型 → 条目 ID 集合（全库扫描，缓存在内存）。</summary>
    private Dictionary<ClipKind, HashSet<long>>? _kindIndex;

    /// <summary>确保类型索引已建好。</summary>
    private async Task<Dictionary<ClipKind, HashSet<long>>> EnsureKindIndexAsync(CancellationToken ct)
    {
        if (_kindIndex is not null) return _kindIndex;

        var index = new Dictionary<ClipKind, HashSet<long>>();
        var imageIds = _imageIds ?? new HashSet<long> { 0 };

        await WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT lID, mText FROM Main WHERE bIsGroup = 0";
            using var r = await cmd.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                var text = r.IsDBNull(1) ? "" : r.GetString(1);
                var kind = Classification.KindClassifier.Classify(text, imageIds.Contains(id));

                if (!index.TryGetValue(kind, out var set))
                    index[kind] = set = new HashSet<long>();
                set.Add(id);
            }

            return 0;
        }, ct);

        _kindIndex = index;
        return index;
    }

    /// <summary>
    /// 扫全库用 KindClassifier 分类，统计各类型数量。
    /// 结果缓存在内存（除非 refresh）。
    /// </summary>
    /// <summary>让类型计数缓存失效（删除/移动后调用）。</summary>
    public void InvalidateKindCounts()
    {
        _kindCountsCache = null;
        _kindIndex = null;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetKindCountsAsync(CancellationToken ct = default)
    {
        if (_kindCountsCache is not null) return _kindCountsCache;

        // 先加载图片 ID 集合，否则 image 会被判成 0
        await GetImageIdsAsync(ct);

        var dict = await WithConnectionAsync(async con =>
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["total"] = 0, ["text"] = 0, ["code"] = 0, ["link"] = 0, ["image"] = 0,
                ["file"] = 0, ["secret"] = 0, ["command"] = 0, ["longtext"] = 0, ["contact"] = 0
            };

            var imageIds = _imageIds ?? new HashSet<long> { 0 };

            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT lID, mText FROM Main WHERE bIsGroup = 0";
            using var r = await cmd.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                var text = r.IsDBNull(1) ? "" : r.GetString(1);
                var isImage = imageIds.Contains(id);

                d["total"]++;

                var kind = Classification.KindClassifier.Classify(text, isImage);
                var key = kind switch
                {
                    ClipKind.Image => "image",
                    ClipKind.Link => "link",
                    ClipKind.Code => "code",
                    ClipKind.File => "file",
                    ClipKind.Secret => "secret",
                    ClipKind.Command => "command",
                    ClipKind.LongText => "longtext",
                    ClipKind.Contact => "contact",
                    _ => "text"
                };
                d[key]++;
            }

            return (IReadOnlyDictionary<string, int>)d;
        }, ct);

        _kindCountsCache = dict;
        return dict;
    }

    public Task<IReadOnlyList<(string Name, int Count)>> GetGroupDistributionAsync(
        int top, CancellationToken ct = default)
    {
        return WithConnectionAsync<IReadOnlyList<(string, int)>>(async con =>
        {
            var list = new List<(string, int)>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT IFNULL(g.mText, '未归类') AS name, COUNT(*) AS n
FROM Main c
LEFT JOIN Main g ON g.lID = c.lParentID AND g.bIsGroup = 1
WHERE c.bIsGroup = 0
GROUP BY c.lParentID
ORDER BY n DESC
LIMIT $top";
            cmd.Parameters.AddWithValue("$top", top);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.IsDBNull(0) ? "未归类" : r.GetString(0), r.GetInt32(1)));
            return list;
        }, ct);
    }

    public Task<IReadOnlyList<(string Day, int Count)>> GetDailyCountsAsync(int days, CancellationToken ct = default)
    {
        return WithConnectionAsync<IReadOnlyList<(string, int)>>(async con =>
        {
            var list = new List<(string, int)>();
            var since = DateTimeOffset.Now.AddDays(-days + 1).Date;
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT date(lDate, 'unixepoch', 'localtime') AS d, COUNT(*) AS n
FROM Main
WHERE bIsGroup = 0 AND lDate >= $since
GROUP BY d ORDER BY d";
            cmd.Parameters.AddWithValue("$since", new DateTimeOffset(since).ToUnixTimeSeconds());

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }, ct);
    }

    public long GetDatabaseSizeBytes()
    {
        try { return new System.IO.FileInfo(_dbPath).Length; }
        catch { return 0; }
    }

    public Task<IReadOnlyList<long>> GetAllClipIdsAsync(CancellationToken ct = default)
    {
        return WithConnectionAsync<IReadOnlyList<long>>(async con =>
        {
            var list = new List<long>(8192);
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT lID FROM Main";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetInt64(0));
            return list;
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetViewCountsAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async con =>
        {
            var dict = new Dictionary<string, int>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND lDate >= $today)                     AS today,
  (SELECT COUNT(DISTINCT lParentID) FROM Data WHERE strClipBoardFormat='CF_DIB')      AS images,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND (mText LIKE 'http%' OR mText LIKE 'www.%')) AS links,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND lParentID = -1)                    AS uncategorized,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND (
      mText LIKE 'sk-%' OR mText LIKE 'github_pat_%' OR mText LIKE 'ghp_%'
      OR mText LIKE 'gho_%' OR mText LIKE 'AKIA%' OR mText LIKE '%PRIVATE KEY%'
      OR mText LIKE 'eyJ%'))                                                         AS secrets,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND (
      mText GLOB '[A-Za-z]:\*' OR mText GLOB '\\*'
      OR mText LIKE '/home/%' OR mText LIKE '/usr/%' OR mText LIKE '/etc/%')) AS files,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND length(mText) < 200
      AND (mText LIKE 'git %' OR mText LIKE 'npm %' OR mText LIKE 'docker %'
        OR mText LIKE 'sudo %' OR mText LIKE 'kubectl %' OR mText LIKE 'dotnet %'
        OR mText LIKE 'pip %' OR mText LIKE 'ssh %' OR mText LIKE 'curl %'))     AS commands,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND length(mText) > 500)           AS longtext,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0 AND length(mText) < 60
      AND (mText LIKE '%@%.%' OR mText GLOB '1[3-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]')) AS contacts,
  (SELECT COUNT(*) FROM Main WHERE bIsGroup=0)                                       AS total";
            var now = DateTimeOffset.Now;
            var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            cmd.Parameters.AddWithValue("$today", midnight.ToUnixTimeSeconds());

            using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                dict["today"] = r.GetInt32(0);
                dict["images"] = r.GetInt32(1);
                dict["links"] = r.GetInt32(2);
                dict["uncategorized"] = r.GetInt32(3);
                dict["secrets"] = r.GetInt32(4);
                dict["files"] = r.GetInt32(5);
                dict["commands"] = r.GetInt32(6);
                dict["longtext"] = r.GetInt32(7);
                dict["contacts"] = r.GetInt32(8);
                dict["total"] = r.GetInt32(9);
            }
            return (IReadOnlyDictionary<string, int>)dict;
        }, ct);
    }

    // ───────────────────────── 原始格式数据（图片解码用）─────────────────────────

    public async Task<byte[]?> GetRawFormatAsync(long clipId, string format, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT ooData FROM Data WHERE lParentID = $id AND strClipBoardFormat = $f LIMIT 1";
            cmd.Parameters.AddWithValue("$id", clipId);
            cmd.Parameters.AddWithValue("$f", format);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v as byte[];
        }, ct);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public void Dispose()
    {
        _con?.Dispose();
        _con = null;
        _gate.Dispose();
    }
}

/// <summary>避免 Domain 层引用 Infrastructure：这里做一个轻量转发。</summary>
internal static class KindClassifierHolder
{
    public static ClipKind Classify(string text, bool isImage) =>
        Classification.KindClassifier.Classify(text, isImage);
}
