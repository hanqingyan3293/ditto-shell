using System.Text.Json;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;


namespace DittoShell.Application.Services;

/// <summary>
/// AI 可调用的工具集（19 个）。
/// 定义和执行分离：Definitions 给 AI 看，ExecuteAsync 真正干活。
/// </summary>
public sealed class AiTools
{
    private readonly IClipReader _reader;
    private readonly ITagStore? _tags;
    private readonly IClipWriter? _writer;
    private readonly ISourceStore? _sources;
    private readonly Func<IReadOnlyList<ClipItem>> _currentSet;

    /// <summary>
    /// 写操作的确认回调。返回 true 才真正执行。
    /// 由 UI 层注入（弹窗询问用户）。
    /// </summary>
    public Func<WriteRequest, Task<bool>>? OnConfirmWrite { get; set; }

    /// <summary>写操作真正执行完之后触发（用于刷新界面）。</summary>
    public event Action? WriteCompleted;

    /// <summary>当前会话的结果集（工具之间的上下文）。</summary>
    public List<ClipItem> Current { get; private set; } = new();

    public AiTools(
        IClipReader reader,
        ITagStore? tags = null,
        IClipWriter? writer = null,
        ISourceStore? sources = null,
        Func<IReadOnlyList<ClipItem>>? currentSet = null)
    {
        _reader = reader;
        _tags = tags;
        _writer = writer;
        _sources = sources;
        _currentSet = currentSet ?? (() => Array.Empty<ClipItem>());
    }

    public void Reset()
    {
        Current = _currentSet().ToList();
    }

    // ═══════════════ 19 个工具定义 ═══════════════

    public static readonly IReadOnlyList<AgentTool> Definitions = new List<AgentTool>
    {
        new()
        {
            Name = "get_context",
            Description = "获取今天日期、条目总数、时间跨度、分组数和标签数。先调这个再决定怎么查。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new()
        {
            Name = "search",
            Description = "按关键词搜索剪贴板历史（自动分词，多个词都要命中）。返回精简结果并成为当前结果集。",
            ParametersJson = """{"type":"object","properties":{"keyword":{"type":"string"},"limit":{"type":"integer","description":"默认30，最大100"}},"required":["keyword"]}"""
        },
        new()
        {
            Name = "search_regex",
            Description = "用正则表达式搜索。适合找 IP、邮箱、URL 等模式化内容。",
            ParametersJson = """{"type":"object","properties":{"pattern":{"type":"string"},"limit":{"type":"integer"}},"required":["pattern"]}"""
        },
        new()
        {
            Name = "filter",
            Description = "在当前结果集上继续筛。所有参数可选，同时给出即 AND。ids 省略则作用于当前结果集。",
            ParametersJson = """{"type":"object","properties":{"time_start":{"type":"string","description":"YYYY-MM-DD"},"time_end":{"type":"string"},"kind":{"type":"string","enum":["text","link","code","file","image","secret","command","longtext","contact"]},"app":{"type":"string"},"group":{"type":"string"},"tag":{"type":"string"},"min_len":{"type":"integer"},"max_len":{"type":"integer"},"exclude":{"type":"string","description":"排除含此词的条目"}}}"""
        },
        new()
        {
            Name = "sort",
            Description = "对当前结果集排序。by 可选 time / length / paste。",
            ParametersJson = """{"type":"object","properties":{"by":{"type":"string","enum":["time","length","paste"]},"desc":{"type":"boolean","description":"默认 true"}},"required":["by"]}"""
        },
        new()
        {
            Name = "page",
            Description = "对当前结果集分页。用于结果太多时逐页看。",
            ParametersJson = """{"type":"object","properties":{"offset":{"type":"integer"},"limit":{"type":"integer","description":"默认20"}},"required":["offset"]}"""
        },
        new()
        {
            Name = "get_detail",
            Description = "看某一条的完整内容、格式、来源程序和时间。",
            ParametersJson = """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}"""
        },
        new()
        {
            Name = "get_details",
            Description = "批量看多条详情（最多 8 条）。",
            ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"integer"}}},"required":["ids"]}"""
        },
        new()
        {
            Name = "list_groups",
            Description = "列出所有分组名（两级用 > 连接）。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new()
        {
            Name = "list_tags",
            Description = "列出所有标签及其条目数。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new()
        {
            Name = "list_apps",
            Description = "列出所有来源程序及其条目数。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new()
        {
            Name = "count",
            Description = "当前结果集的条目数。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new()
        {
            Name = "group_by",
            Description = "按维度统计当前结果集。field 可选 day / kind / app / group。",
            ParametersJson = """{"type":"object","properties":{"field":{"type":"string","enum":["day","kind","app","group"]}},"required":["field"]}"""
        },
        new()
        {
            Name = "timeline",
            Description = "按天统计当前结果集的分布，看什么时间复制最多。",
            ParametersJson = """{"type":"object","properties":{"days":{"type":"integer","description":"最近几天，默认30"}}}"""
        },
        new()
        {
            Name = "find_similar",
            Description = "找和指定条目相似的其它条目（按内容特征）。",
            ParametersJson = """{"type":"object","properties":{"id":{"type":"integer"},"limit":{"type":"integer","description":"默认10"}},"required":["id"]}"""
        },
        new()
        {
            Name = "detect_sensitive",
            Description = "扫描出现凭据/密钥/密码的条目，返回数量和列表。",
            ParametersJson = """{"type":"object","properties":{"limit":{"type":"integer","description":"默认30"}}}"""
        },
        new()
        {
            Name = "export",
            Description = "把当前结果集导出为文本或 JSON 文件。会请求用户确认。",
            ParametersJson = """{"type":"object","properties":{"format":{"type":"string","enum":["txt","json"]}}}"""
        },
        new()
        {
            Name = "move_to_group",
            Description = "把指定条目移到某个分组。会请求用户确认。",
            ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"integer"}},"group":{"type":"string"}},"required":["ids","group"]}"""
        },
        new()
        {
            Name = "add_tag",
            Description = "给指定条目加标签。会请求用户确认。",
            ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"integer"}},"tag":{"type":"string"}},"required":["ids","tag"]}"""
        },
        new()
        {
            Name = "mark_private",
            Description = "把指定条目标记为隐私（不上传 AI）。会请求用户确认。",
            ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"integer"}}},"required":["ids"]}"""
        }
    };

    /// <summary>一次写操作的请求。</summary>
    public sealed class WriteRequest
    {
        public string Tool { get; init; } = "";        // move_to_group / add_tag / mark_private / export
        public List<long> Ids { get; init; } = new();
        public string? Group { get; init; }
        public string? Tag { get; init; }
        public string? Format { get; init; }

        public string Describe() => Tool switch
        {
            "move_to_group" => $"把 {Ids.Count} 条移到分组「{Group}」",
            "add_tag" => $"给 {Ids.Count} 条加标签 #{Tag}",
            "mark_private" => $"把 {Ids.Count} 条标记为隐私",
            "export" => $"导出 {Ids.Count} 条为 {Format?.ToUpperInvariant()}",
            _ => Tool
        };
    }

    /// <summary>需要用户确认的写操作。</summary>
    public static bool IsWriteTool(string name)
        => name is "export" or "move_to_group" or "add_tag" or "mark_private";

    // ═══════════════ 执行 ═══════════════

    /// <summary>执行一个工具，返回给 AI 的精简文本。</summary>
    public async Task<string> ExecuteAsync(string name, string argsJson, CancellationToken ct = default)
    {
        JsonElement a;
        try { a = JsonDocument.Parse(argsJson).RootElement; }
        catch { a = default; }

        try
        {
            return name switch
            {
                "get_context" => await GetContextAsync(ct),
                "search" => await SearchAsync(Str(a, "keyword"), Int(a, "limit", 30), ct),
                "search_regex" => await SearchRegexAsync(Str(a, "pattern"), Int(a, "limit", 30), ct),
                "filter" => await FilterAsync(a, ct),
                "sort" => Sort(Str(a, "by"), Bool(a, "desc", true)),
                "page" => Page(Int(a, "offset", 0), Int(a, "limit", 20)),
                "get_detail" => await GetDetailAsync(Int64(a, "id"), ct),
                "get_details" => await GetDetailsAsync(Ids(a, "ids"), ct),
                "list_groups" => await ListGroupsAsync(ct),
                "list_tags" => await ListTagsAsync(ct),
                "list_apps" => await ListAppsAsync(ct),
                "count" => Count(),
                "group_by" => GroupBy(Str(a, "field")),
                "timeline" => Timeline(Int(a, "days", 30)),
                "find_similar" => FindSimilar(Int64(a, "id"), Int(a, "limit", 10)),
                "detect_sensitive" => await DetectSensitiveAsync(Int(a, "limit", 30), ct),
                "export" => await ExportAsync(Str(a, "format"), ct),
                "move_to_group" => await MoveToGroupAsync(Ids(a, "ids"), Str(a, "group"), ct),
                "add_tag" => await AddTagAsync(Ids(a, "ids"), Str(a, "tag"), ct),
                "mark_private" => await MarkPrivateAsync(Ids(a, "ids"), ct),
                _ => "未知工具：" + name
            };
        }
        catch (Exception ex)
        {
            return "工具执行失败：" + ex.Message;
        }
    }

    // ───────── 各工具实现 ─────────

    private async Task<string> GetContextAsync(CancellationToken ct)
    {
        var all = await StripPrivateAsync(await _reader.GetLatestAsync(5, null), ct);
        all = all.Take(1).ToList();
        var total = await _reader.GetViewCountsAsync();
        var groups = await _reader.GetGroupTreeAsync();
        var tags = _tags is null ? new List<(string, int)>() : await _tags.GetAllTagsAsync();

        int groupCount = 0;
        void Count(IEnumerable<GroupNode> ns)
        {
            foreach (var n in ns) { groupCount++; Count(n.Children); }
        }
        Count(groups);

        var newest = all.FirstOrDefault();
        var sb = new System.Text.StringBuilder();
        sb.Append("today=").Append(DateTime.Today.ToString("yyyy-MM-dd"));
        sb.Append(" weekday=").Append(DateTime.Today.DayOfWeek);
        sb.Append(" total=").Append(total.GetValueOrDefault("total"));
        sb.Append(" groups=").Append(groupCount);
        sb.Append(" tags=").Append(tags.Count(t => t.Item1 != "__private"));
        if (newest is not null)
            sb.Append(" newest=").Append(newest.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        return sb.ToString();
    }

    private HashSet<long>? _privateCache;

    /// <summary>隐私条目 ID 集合（AI 一律不可见）。</summary>
    private async Task<HashSet<long>> GetPrivateIdsAsync(CancellationToken ct)
    {
        if (_privateCache is not null) return _privateCache;

        try
        {
            if (_tags is null) { _privateCache = new HashSet<long>(); return _privateCache; }

            var hits = await _tags.GetClipIdsByTagsAsync(
                new List<string> { "__private" }, false);
            _privateCache = hits.Select(h => h.ClipId).ToHashSet();
        }
        catch { _privateCache = new HashSet<long>(); }

        return _privateCache;
    }

    /// <summary>去掉隐私条目。</summary>
    private async Task<List<Domain.Models.ClipItem>> StripPrivateAsync(
        IEnumerable<Domain.Models.ClipItem> items, CancellationToken ct)
    {
        var priv = await GetPrivateIdsAsync(ct);
        return items.Where(x => !priv.Contains(x.Id)).ToList();
    }

    private async Task<string> SearchAsync(string keyword, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return "关键词为空";
        limit = Math.Clamp(limit, 1, 100);

        var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 1) 先整句
        var hits = await _reader.SearchAsync(keyword, null, null, null, null, ct);

        // 2) 整句没结果 → 逐词 OR 合并
        if (hits.Count == 0 && words.Length > 1)
        {
            var merged = new List<ClipItem>();
            var seen = new HashSet<long>();
            foreach (var w in words)
            {
                if (w.Length < 2) continue;
                var part = await _reader.SearchAsync(w, null, null, null, null, ct);
                foreach (var p in part)
                    if (seen.Add(p.Id)) merged.Add(p);
            }
            hits = merged;
        }

        // 3) 还是没结果 → 取关键词的前 2 字再试（应对长词）
        if (hits.Count == 0 && keyword.Length >= 3)
        {
            hits = await _reader.SearchAsync(keyword[..2], null, null, null, null, ct);
        }

        var visible = await StripPrivateAsync(hits, ct);
        Current = visible.Take(limit).ToList();
        return Format();
    }

    private async Task<string> SearchRegexAsync(string pattern, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "正则为空";
        limit = Math.Clamp(limit, 1, 100);

        System.Text.RegularExpressions.Regex rx;
        try { rx = new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
        catch (Exception ex) { return "正则无效：" + ex.Message; }

        var pool = await StripPrivateAsync(await _reader.GetLatestAsync(3000, null), ct);
        Current = pool.Where(x => rx.IsMatch(x.Preview ?? "")).Take(limit).ToList();
        return Format();
    }

    private async Task<string> FilterAsync(JsonElement a, CancellationToken ct)
    {
        var set = Current.Count > 0
            ? Current
            : await StripPrivateAsync(await _reader.GetLatestAsync(2000, null), ct);

        var tStart = Str(a, "time_start");
        var tEnd = Str(a, "time_end");
        var kind = Str(a, "kind");
        var app = Str(a, "app");
        var group = Str(a, "group");
        var tag = Str(a, "tag");
        var minLen = Int(a, "min_len", 0);
        var maxLen = Int(a, "max_len", 0);
        var exclude = Str(a, "exclude");

        IEnumerable<ClipItem> q = set;

        if (tStart.Length == 10 && DateTime.TryParse(tStart, out var ds))
            q = q.Where(x => x.CreatedAt.LocalDateTime.Date >= ds.Date);
        if (tEnd.Length == 10 && DateTime.TryParse(tEnd, out var de))
            q = q.Where(x => x.CreatedAt.LocalDateTime.Date <= de.Date);

        if (kind.Length > 0)
        {
            var k = kind switch
            {
                "text" => ClipKind.Text, "link" => ClipKind.Link, "code" => ClipKind.Code,
                "file" => ClipKind.File, "image" => ClipKind.Image, "secret" => ClipKind.Secret,
                "command" => ClipKind.Command, "longtext" => ClipKind.LongText,
                "contact" => ClipKind.Contact, _ => (ClipKind?)null
            };
            if (k is not null) q = q.Where(x => x.Kind == k.Value);
        }

        if (minLen > 0) q = q.Where(x => x.CharCount >= minLen);
        if (maxLen > 0) q = q.Where(x => x.CharCount <= maxLen);
        if (exclude.Length > 0)
            q = q.Where(x => !(x.Preview ?? "").Contains(exclude, StringComparison.OrdinalIgnoreCase));

        var list = q.ToList();

        if (tag.Length > 0 && _tags is not null)
        {
            var ids = await _tags.GetClipIdsByTagsAsync(new List<string> { tag }, false);
            var set2 = ids.Select(x => x.ClipId).ToHashSet();
            list = list.Where(x => set2.Contains(x.Id)).ToList();
        }

        if (group.Length > 0)
        {
            var tree = await _reader.GetGroupTreeAsync();
            long? gid = null;
            void Find(IEnumerable<GroupNode> ns)
            {
                foreach (var n in ns)
                {
                    if (n.Name.Equals(group, StringComparison.OrdinalIgnoreCase)) gid = n.Id;
                    Find(n.Children);
                }
            }
            Find(tree);
            if (gid is not null) list = list.Where(x => x.GroupId == gid).ToList();
        }

        Current = list;
        return Format();
    }

    private string Sort(string by, bool desc)
    {
        Current = by switch
        {
            "length" => (desc ? Current.OrderByDescending(x => x.CharCount)
                              : Current.OrderBy(x => x.CharCount)).ToList(),
            "paste" => (desc ? Current.OrderByDescending(x => x.LastPasteDate)
                             : Current.OrderBy(x => x.LastPasteDate)).ToList(),
            _ => (desc ? Current.OrderByDescending(x => x.CreatedAt)
                       : Current.OrderBy(x => x.CreatedAt)).ToList()
        };
        return Format();
    }

    private string Page(int offset, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        var total = Current.Count;
        Current = Current.Skip(Math.Max(0, offset)).Take(limit).ToList();
        return $"共 {total} 条，第 {offset + 1} 起取 {Current.Count} 条" + Environment.NewLine + Format();
    }

    private async Task<string> GetDetailAsync(long id, CancellationToken ct)
    {
        if (id <= 0) return "id 无效";
        var d = await _reader.GetDetailAsync(id, ct);
        if (d is null) return $"没有 id={id} 的条目";

        var text = d.FullText ?? "";
        if (text.Length > 2000) text = text[..2000] + "…（已截断）";

        return $"id={id}" + Environment.NewLine
             + "time=" + d.Item.CreatedAt.ToString("yyyy-MM-dd HH:mm") + Environment.NewLine
             + "kind=" + d.Item.Kind + Environment.NewLine
             + "formats=" + string.Join(",", d.Formats.Take(6)) + Environment.NewLine
             + "content:" + Environment.NewLine + text;
    }

    private async Task<string> GetDetailsAsync(List<long> ids, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in ids.Take(8))
            sb.AppendLine(await GetDetailAsync(id, ct)).AppendLine("---");
        return sb.ToString();
    }

    private async Task<string> ListGroupsAsync(CancellationToken ct)
    {
        var tree = await _reader.GetGroupTreeAsync();
        var names = new List<string>();
        void Walk(IEnumerable<GroupNode> ns, string prefix)
        {
            foreach (var n in ns)
            {
                var path = prefix.Length == 0 ? n.Name : prefix + ">" + n.Name;
                names.Add(path);
                Walk(n.Children, path);
            }
        }
        Walk(tree, "");
        return names.Count == 0 ? "没有分组" : string.Join(Environment.NewLine, names);
    }

    private async Task<string> ListTagsAsync(CancellationToken ct)
    {
        if (_tags is null) return "标签服务不可用";
        var all = await _tags.GetAllTagsAsync();
        var real = all.Where(t => t.Item1 != "__private").OrderByDescending(t => t.Item2).Take(40).ToList();
        return real.Count == 0 ? "没有标签"
            : string.Join(Environment.NewLine, real.Select(t => $"{t.Item1}|{t.Item2}"));
    }

    private async Task<string> ListAppsAsync(CancellationToken ct)
    {
        if (_sources is null) return "来源数据不可用";

        var dist = await _sources.GetSourceDistributionAsync(30);
        return dist.Count == 0 ? "没有来源记录"
            : string.Join(Environment.NewLine, dist.Select(d => $"{d.App}|{d.Count}"));
    }

    private string Count() => "当前结果集 " + Current.Count + " 条";

    private string GroupBy(string field)
    {
        IEnumerable<(string Key, int Count)> g = field switch
        {
            "kind" => Current.GroupBy(x => x.Kind.ToString())
                .Select(x => (x.Key, x.Count())),
            "day" => Current.GroupBy(x => x.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd"))
                .Select(x => (Key: x.Key, Cnt: x.Count())).OrderByDescending(x => x.Key),
            _ => Current.GroupBy(x => x.Kind.ToString()).Select(x => (Key: x.Key, Cnt: x.Count()))
        };
        return string.Join(Environment.NewLine, g.Select(x => $"{x.Key}|{x.Count}"));
    }

    private string Timeline(int days)
    {
        days = Math.Clamp(days, 1, 365);
        var from = DateTime.Today.AddDays(-days + 1);

        var rows = Current
            .Where(x => x.CreatedAt.LocalDateTime.Date >= from)
            .GroupBy(x => x.CreatedAt.LocalDateTime.ToString("MM-dd"))
            .Select(g => new { Day = g.Key, N = g.Count() })
            .OrderBy(x => x.Day)
            .ToList();

        if (rows.Count == 0) return "该时间段没有条目";
        return string.Join(Environment.NewLine, rows.Select(r => r.Day + "|" + r.N));
    }

    private string FindSimilar(long id, int limit)
    {
        var src = Current.FirstOrDefault(x => x.Id == id)
                  ?? Current.FirstOrDefault();
        if (src is null) return "找不到该条目";

        // 简单特征：取前 20 个字符的二元组做交集
        var key = (src.Preview ?? "").Take(40).ToArray();
        var grams = new HashSet<string>();
        for (int i = 0; i + 2 <= key.Length; i++) grams.Add(new string(key, i, 2));

        var scored = Current
            .Where(x => x.Id != id)
            .Select(x =>
            {
                var t = (x.Preview ?? "").Take(40).ToArray();
                var g2 = new HashSet<string>();
                for (int i = 0; i + 2 <= t.Length; i++) g2.Add(new string(t, i, 2));
                return (Item: x, Score: grams.Intersect(g2).Count());
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(limit, 1, 20))
            .Select(x => x.Item)
            .ToList();

        Current = scored;
        return Format();
    }

    private async Task<string> DetectSensitiveAsync(int limit, CancellationToken ct)
    {
        var pool = Current.Count > 0 ? Current : (await _reader.GetLatestAsync(3000, null)).ToList();
        var hits = pool.Where(x => x.Kind == ClipKind.Secret).Take(Math.Clamp(limit, 1, 100)).ToList();
        Current = hits;
        return $"发现 {hits.Count} 条疑似凭据" + Environment.NewLine + Format();
    }

    /// <summary>精简格式：id|日期|类型|前40字（省 token）。</summary>
    private string Format()
    {
        if (Current.Count == 0) return "没有匹配结果";

        var sb = new System.Text.StringBuilder();
        sb.Append("共 ").Append(Current.Count).Append(" 条（id|日期|类型|摘要）").AppendLine();

        foreach (var x in Current.Take(40))
        {
            var p = (x.Preview ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (p.Length > 40) p = p[..40] + "…";
            sb.Append(x.Id).Append('|')
              .Append(x.CreatedAt.ToString("MM-dd")).Append('|')
              .Append(x.Kind).Append('|')
              .AppendLine(p);
        }

        if (Current.Count > 40) sb.Append("（只列出前 40 条，可用 page 翻页）");
        return sb.ToString();
    }

    // ───────── 写操作（需确认）─────────

    private async Task<bool> ConfirmAsync(WriteRequest req)
    {
        if (OnConfirmWrite is null) return false;
        try { return await OnConfirmWrite(req); }
        catch { return false; }
    }

    private async Task<string> MoveToGroupAsync(List<long> ids, string group, CancellationToken ct)
    {
        if (ids.Count == 0) return "没有指定条目";
        if (string.IsNullOrWhiteSpace(group)) return "没有指定分组";

        var ok = await ConfirmAsync(new WriteRequest
        { Tool = "move_to_group", Ids = ids, Group = group });
        if (!ok) return "用户取消了这次操作";

        if (_writer is null) return "写入通道不可用";

        try
        {
            // 支持「父>子」两级路径：逐级查找，缺哪级建哪级
            var parts = group.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            long parentId = 0;
            long? gid = null;

            for (int level = 0; level < parts.Length; level++)
            {
                var name = parts[level];
                var tree = await _reader.GetGroupTreeAsync();

                long? found = null;
                void Find(IEnumerable<GroupNode> ns, long parent)
                {
                    foreach (var n in ns)
                    {
                        // 只在当前层级里找同名
                        if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && parent == parentId)
                            found = n.Id;
                        Find(n.Children, n.Id);
                    }
                }
                Find(tree, 0);

                if (found is null)
                {
                    var created = await _writer.CreateGroupAsync(name, parentId, ct);
                    if (created <= 0) return $"创建分组「{name}」失败";
                    found = created;
                }

                parentId = found.Value;
                gid = found;
            }

            if (gid is null) return "创建分组失败";

            foreach (var id in ids.Take(200))
                await _writer.MoveToGroupAsync(new[] { id }, gid.Value, ct);

            WriteCompleted?.Invoke();
            return $"已把 {ids.Count} 条移到「{group}」";
        }
        catch (Exception ex) { return "移动失败：" + ex.Message; }
    }

    private async Task<string> AddTagAsync(List<long> ids, string tag, CancellationToken ct)
    {
        if (ids.Count == 0) return "没有指定条目";
        if (string.IsNullOrWhiteSpace(tag)) return "没有指定标签";

        var ok = await ConfirmAsync(new WriteRequest
        { Tool = "add_tag", Ids = ids, Tag = tag });
        if (!ok) return "用户取消了这次操作";

        if (_tags is null) return "标签服务不可用";

        try
        {
            foreach (var id in ids.Take(200))
                await _tags.AddTagAsync(id, tag.TrimStart('#'), ct);

            WriteCompleted?.Invoke();
            return $"已给 {ids.Count} 条加上 #{tag}";
        }
        catch (Exception ex) { return "加标签失败：" + ex.Message; }
    }

    private async Task<string> MarkPrivateAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return "没有指定条目";

        var ok = await ConfirmAsync(new WriteRequest
        { Tool = "mark_private", Ids = ids });
        if (!ok) return "用户取消了这次操作";

        if (_tags is null) return "标签服务不可用";

        try
        {
            foreach (var id in ids.Take(200))
                await _tags.AddTagAsync(id, "__private", ct);

            WriteCompleted?.Invoke();
            return $"已把 {ids.Count} 条标记为隐私";
        }
        catch (Exception ex) { return "标记失败：" + ex.Message; }
    }

    private Task<string> ExportAsync(string format, CancellationToken ct)
    {
        if (Current.Count == 0) return Task.FromResult("当前没有可导出的结果");

        // 导出需要 UI 交互（保存对话框），交给上层
        var ids = Current.Select(x => x.Id).ToList();
        _ = ConfirmAsync(new WriteRequest
        { Tool = "export", Ids = ids, Format = format });

        return Task.FromResult($"已请求导出 {ids.Count} 条（{format}），请在弹窗里确认");
    }

    // ───────── JSON 辅助 ─────────

    private static string Str(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement a, string k, int def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static long Int64(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static bool Bool(JsonElement a, string k, bool def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
           ? v.GetBoolean() : def;

    private static List<long> Ids(JsonElement a, string k)
    {
        var list = new List<long>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
            && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var one in v.EnumerateArray())
                if (one.ValueKind == JsonValueKind.Number) list.Add(one.GetInt64());
        }
        return list;
    }
}
