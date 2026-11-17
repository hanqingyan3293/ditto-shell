using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.Infrastructure.Stubs;

/// <summary>
/// 骨架阶段的假数据源。真实实现 DittoDbReader 就绪后由 DI 替换。
/// 数据取自用户真实库的样例，保证界面观感与真实场景一致。
/// </summary>
public sealed class StubClipReader : IClipReader
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static readonly ClipItem[] Items =
    {
        new(9793, "https://github.com/mg-chao/snow-shot", -1, Now.AddMinutes(-3), ClipKind.Link, 38, 1001),
        new(9792, "不做云同步、不做多机、不做手机端、不做密码管理。", -1, Now.AddMinutes(-8), ClipKind.Text, 24, 1002),
        new(9791, "gh auth login --with-token < token.txt", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(24), ClipKind.Code, 44, 1003),
        new(9789, "gh auth status", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(24), ClipKind.Code, 14, 1004),
        new(9788, "gh auth setup-git", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(24), ClipKind.Code, 17, 1005),
        new(9783, "github_pat_11BIJ••••••••••••••••", -1, Now.Date.AddDays(-1).AddHours(1).AddMinutes(47), ClipKind.Secret, 93, 1006),
        new(9786, "密钥怎么交给我（按安全度排序）方案 A（推荐）：本地文件，不进聊天。在项目里先建忽略规则…", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(23), ClipKind.Text, 1200, 1007),
        new(9784, @"D:\Project", -1, Now.Date.AddDays(-1).AddHours(1).AddMinutes(49), ClipKind.File, 21, 1008),
        new(9790, "PS C:\\Users\\YourName> Get-Content \"D:\\File\\GitHub\\tokens\\token.txt\" | gh auth login", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(25), ClipKind.Code, 462, 1009),
        new(9787, "截图 2026-11-15 052411.png", -1, Now.Date.AddDays(-1).AddHours(5).AddMinutes(31), ClipKind.Image, 0, 1010),
        new(9785, "token.txt", -1, Now.Date.AddDays(-1).AddHours(1).AddMinutes(49), ClipKind.File, 13, 1011),
        new(9782, "git push origin main", -1, Now.Date.AddDays(-2), ClipKind.Code, 19, 1012),
        new(9781, "SELECT lID, mText FROM Main WHERE lParentID = -1 ORDER BY clipOrder DESC", -1, Now.Date.AddDays(-2), ClipKind.Code, 70, 1013),
        new(9780, "https://github.com/sabrogden/Ditto", -1, Now.Date.AddDays(-2), ClipKind.Link, 36, 1014),
        new(9779, "dotnet build -c Release", -1, Now.Date.AddDays(-2), ClipKind.Code, 25, 1015),
        new(9778, "功能矩阵：P0 必须实现 / P1 核心闭环 / P2 v2 再做 / P3 坚决不做", -1, Now.Date.AddDays(-3), ClipKind.Text, 42, 1016),
        new(9777, @"C:\Users\YourName\.dsh\skills", -1, Now.Date.AddDays(-3), ClipKind.File, 38, 1017),
        new(9776, "你的剪贴板历史有 6,179 条，其中 830MB 主要是图片", -1, Now.Date.AddDays(-3), ClipKind.Text, 34, 1018),
        new(9775, "npm i lucide-static --no-audit --no-fund", -1, Now.Date.AddDays(-3), ClipKind.Code, 42, 1019),
        new(9774, "https://api-docs.deepseek.com/api/list-models/", -1, Now.Date.AddDays(-4), ClipKind.Link, 48, 1020),
        new(9773, "深色模式 v1 就做；统计页迁到外壳", -1, Now.Date.AddDays(-4), ClipKind.Text, 20, 1021),
        new(9772, "47 条边界情况：数据 12 / Ditto 8 / 系统 9 / 用户 10 / 网络 10 / 磁盘 7 / 时间 4", -1, Now.Date.AddDays(-4), ClipKind.Text, 60, 1022),
    };

    public Task<IReadOnlyList<ClipItem>> GetLatestAsync(int limit, long? groupId, CancellationToken ct = default)
    {
        IEnumerable<ClipItem> q = Items;
        if (groupId is > 0) q = q.Where(x => x.GroupId == groupId);
        return Task.FromResult<IReadOnlyList<ClipItem>>(q.Take(limit).ToList());
    }

    public Task<IReadOnlyList<ClipItem>> SearchAsync(
        string query, long? groupId, ClipKind? kind,
        string? tagFilter = null, string? fromFilter = null,
        CancellationToken ct = default)
    {
        IEnumerable<ClipItem> q = Items;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(x => terms.All(t => x.Preview.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        if (kind is not null) q = q.Where(x => x.Kind == kind);
        if (groupId is > 0) q = q.Where(x => x.GroupId == groupId);
        return Task.FromResult<IReadOnlyList<ClipItem>>(q.ToList());
    }

    public Task<ClipDetail?> GetDetailAsync(long clipId, CancellationToken ct = default)
    {
        var item = Items.FirstOrDefault(x => x.Id == clipId);
        if (item is null) return Task.FromResult<ClipDetail?>(null);

        var detail = new ClipDetail(
            item,
            item.Preview,
            item.Kind switch
            {
                ClipKind.Link => new[] { "CF_UNICODETEXT", "HTML Format" },
                ClipKind.Code => new[] { "CF_UNICODETEXT" },
                ClipKind.Image => new[] { "CF_DIB" },
                ClipKind.File => new[] { "CF_HDROP", "CF_UNICODETEXT" },
                _ => new[] { "CF_UNICODETEXT", "CF_TEXT" }
            },
            item.Kind == ClipKind.Image ? 8_674_984L : 128L,
            (int)(item.Id % 4),
            item.CreatedAt);
        return Task.FromResult<ClipDetail?>(detail);
    }

    public Task<IReadOnlyList<GroupNode>> GetGroupTreeAsync(CancellationToken ct = default)
    {
        var gun = new GroupNode { Id = 26, Name = "枪械库", ParentId = -1, Count = 0 };
        gun.Children.Add(new GroupNode { Id = 27, Name = "KC17", ParentId = 26, Count = 30 });
        gun.Children.Add(new GroupNode { Id = 29, Name = "M4A1", ParentId = 26, Count = 18 });
        gun.Children.Add(new GroupNode { Id = 28, Name = "PKM", ParentId = 26, Count = 11 });
        gun.Children.Add(new GroupNode { Id = 63, Name = "ASVAL", ParentId = 26, Count = 13 });

        var list = new List<GroupNode> { gun };
        list.Add(new GroupNode { Id = 100, Name = "工作", ParentId = -1, Count = 42 });
        list.Add(new GroupNode { Id = 101, Name = "参考", ParentId = -1, Count = 15 });
        return Task.FromResult<IReadOnlyList<GroupNode>>(list);
    }

    public Task<long> GetMaxClipIdAsync(CancellationToken ct = default) => Task.FromResult(9793L);

    public Task<int> GetTotalCountAsync(CancellationToken ct = default) => Task.FromResult(6135);

    public Task<IReadOnlyDictionary<string, int>> GetKindCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
        {
            ["total"] = Items.Count(), ["text"] = Items.Count()
        });

    public Task<IReadOnlyList<(string Name, int Count)>> GetGroupDistributionAsync(
        int top, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string, int)>>(new List<(string, int)>());

    public Task<IReadOnlyList<(string Day, int Count)>> GetDailyCountsAsync(int days, CancellationToken ct = default)
    {
        var list = new List<(string, int)>();
        for (int i = days - 1; i >= 0; i--)
            list.Add((DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd"), 20 + (i * 7) % 60));
        return Task.FromResult<IReadOnlyList<(string, int)>>(list);
    }

    public long GetDatabaseSizeBytes() => 830 * 1024L * 1024L;

    public Task<IReadOnlyList<long>> GetAllClipIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<long>>(Items.Select(x => x.Id).ToList());

    public Task<IReadOnlyDictionary<string, int>> GetViewCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
        {
            ["today"] = 48, ["images"] = 113, ["links"] = 2834, ["uncategorized"] = 412, ["total"] = 6135
        });

    public Task<byte[]?> GetRawFormatAsync(long clipId, string format, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);
}
