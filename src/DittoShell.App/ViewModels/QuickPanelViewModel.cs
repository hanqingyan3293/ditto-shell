using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DittoShell.App.Localization;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.App.ViewModels;

/// <summary>斜杠面板里的一条命令。</summary>
public sealed class SlashItemVM
{
    public SlashItemVM(string icon, string name, string alias, string hint, bool hasChildren = false, string colorKey = "B.Text2")
    {
        Icon = icon; Name = name; Alias = alias; Hint = hint; HasChildren = hasChildren;
        Color = System.Windows.Application.Current?.TryFindResource(colorKey) as Brush ?? Brushes.Gray;
    }

    public string Icon { get; }
    public string Name { get; }
    public string Alias { get; }
    public string Hint { get; }
    public bool HasChildren { get; }
    public Brush Color { get; }
}

public sealed partial class QuickPanelViewModel : ObservableObject
{
    private readonly IClipReader _reader;
    private readonly Media.ThumbnailCache _thumbs;
    private readonly DittoShell.Application.Services.GroupService? _groups;
    private readonly IDittoBridge? _bridge;

    public QuickPanelViewModel(IClipReader reader, Media.ThumbnailCache thumbs,
                               DittoShell.Application.Services.GroupService? groups = null,
                               IDittoBridge? bridge = null)
    {
        _reader = reader;
        _thumbs = thumbs;
        _groups = groups;
        _bridge = bridge;
        _ = ReloadAsync();
        BuildSlashItems();
    }

    public ObservableCollection<ClipRowVM> Items { get; } = new();
    public ObservableCollection<GroupRowVM> Groups { get; } = new();
    public ObservableCollection<SlashItemVM> SlashItems { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ClipRowVM? _selected;
    [ObservableProperty] private bool _isGroupOpen;
    [ObservableProperty] private bool _isDetailOpen;

    /// <summary>详情列里显示的完整内容。</summary>
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _detailLoading;
    [ObservableProperty] private bool _isSlashOpen;

    /// <summary>已选中的搜索前缀（tag: / type: / app: 等）。</summary>
    [ObservableProperty] private string _activePrefix = "";
    [ObservableProperty] private bool _hasPrefix;

    partial void OnActivePrefixChanged(string value)
    {
        HasPrefix = !string.IsNullOrEmpty(value);
    }

    [RelayCommand]
    private void ClearPrefix()
    {
        ActivePrefix = "";
        SearchText = "";
        FootHint = "前缀已清除";
    }
    [ObservableProperty] private string _footHint = "";

    /// <summary>粘贴指定条目（交给 Ditto 完成格式还原）。</summary>
    public async Task<bool> PasteAsync(long clipId, bool plainText)
    {
        if (_bridge is null) { FootHint = "未找到 Ditto，无法粘贴"; return false; }
        try
        {
            await _bridge.PasteAsync(clipId, plainText);
            return true;
        }
        catch (Exception ex)
        {
            FootHint = $"粘贴失败：{ex.Message}";
            return false;
        }
    }

    // ───────── 分组写操作（B3）─────────

    public bool CanWriteGroups => _groups is not null;

    public async Task<DittoShell.Application.Services.OpResult> CreateGroupAsync(string name, long parentId)
    {
        if (_groups is null) return DittoShell.Application.Services.OpResult.Fail("未接通数据库写入");
        var r = await _groups.CreateAsync(name, parentId);
        if (r.Success) await RefreshGroupsAsync();
        return r;
    }

    public async Task<DittoShell.Application.Services.OpResult> RenameGroupAsync(long id, string name)
    {
        if (_groups is null) return DittoShell.Application.Services.OpResult.Fail("未接通数据库写入");
        var r = await _groups.RenameAsync(id, name);
        if (r.Success) await RefreshGroupsAsync();
        return r;
    }

    public async Task<DittoShell.Application.Services.OpResult> DeleteGroupAsync(long id)
    {
        if (_groups is null) return DittoShell.Application.Services.OpResult.Fail("未接通数据库写入");
        var r = await _groups.DeleteAsync(id);
        if (r.Success) await RefreshGroupsAsync();
        return r;
    }

    public async Task<DittoShell.Application.Services.OpResult> MoveGroupToTopAsync(long id)
    {
        if (_groups is null) return DittoShell.Application.Services.OpResult.Fail("未接通数据库写入");
        var r = await _groups.ReparentAsync(id, -1);
        if (r.Success) await RefreshGroupsAsync();
        return r;
    }

    private List<Domain.Models.GroupNode> _groupTree = new();
    private readonly Dictionary<long, bool> _expanded = new();

    [RelayCommand]
    private void ToggleGroupExpand(GroupRowVM? row)
    {
        if (row is null || !row.HasChildren) return;
        row.IsExpanded = !row.IsExpanded;
        _expanded[row.Id] = row.IsExpanded;
        ApplyGroups();
    }

    private async Task RefreshGroupsAsync()
    {
        _groupTree = (await _reader.GetGroupTreeAsync()).ToList();
        ApplyGroups();
    }

    private void ApplyGroups()
    {
        var flat = new List<GroupRowVM>();
        foreach (var n in _groupTree)
        {
            var parent = new GroupRowVM(n.Id, n.Name, n.Count, "Folder", hasChildren: n.Children.Count > 0);
            parent.IsExpanded = _expanded.TryGetValue(n.Id, out var e) ? e : true;
            flat.Add(parent);

            if (parent.IsExpanded)
            {
                foreach (var c in n.Children)
                    flat.Add(new GroupRowVM(c.Id, c.Name, c.Count, "Circle", indent: 1));
            }
        }

        // 保留虚拟视图（前 6 项），替换后面的真实分组
        while (Groups.Count > 6) Groups.RemoveAt(Groups.Count - 1);
        foreach (var g in flat) Groups.Add(g);
    }

    public Task<ClipDetail?> GetDetailAsync(long id) => _reader.GetDetailAsync(id);
    public Task<byte[]?> GetDibAsync(long id) => _reader.GetRawFormatAsync(id, "CF_DIB");

    /// <summary>由 View 注入：切到主界面。</summary>
    public Action? RequestManage { get; set; }

    /// <summary>由 View 注入：关闭面板。</summary>
    public Action? RequestClose { get; set; }

    public void Reload()
    {
        SearchText = "";
        IsGroupOpen = false;
        IsSlashOpen = false;
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var items = await _reader.GetLatestAsync(500, null);
        Items.Clear();
        foreach (var i in items) Items.Add(new ClipRowVM(i));
        NumberItems();
        Selected = Items.FirstOrDefault();

        if (Groups.Count == 0)
        {
            var counts = await _reader.GetViewCountsAsync();
            Groups.Add(new GroupRowVM(-1, L.T("main.view.all"), counts.GetValueOrDefault("total"), "Layers") { IsSelected = true });
            Groups.Add(new GroupRowVM(-2, L.T("main.view.today"), counts.GetValueOrDefault("today"), "Clock"));
            Groups.Add(new GroupRowVM(-3, L.T("main.view.images"), counts.GetValueOrDefault("images"), "Image"));
            Groups.Add(new GroupRowVM(-4, L.T("main.view.links"), counts.GetValueOrDefault("links"), "Link"));
            Groups.Add(new GroupRowVM(-5, L.T("main.view.code"), 0, "Code"));
            Groups.Add(new GroupRowVM(-6, L.T("main.view.uncategorized"), counts.GetValueOrDefault("uncategorized"), "Filter"));

            var tree = await _reader.GetGroupTreeAsync();
            foreach (var g in tree)
            {
                Groups.Add(new GroupRowVM(g.Id, g.Name, g.Count, "Folder"));
                foreach (var c in g.Children)
                    Groups.Add(new GroupRowVM(c.Id, c.Name, c.Count, "ChevronRight", indent: 1));
            }
        }
    }

    private void BuildSlashItems()
    {
        SlashItems.Clear();
        foreach (var item in SlashCatalog.All) SlashItems.Add(item);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppress) return;            // 程序自己改的，不触发搜索

        // 输入 "/" 立刻弹斜杠面板
        IsSlashOpen = value.TrimStart().StartsWith("/");
        if (IsSlashOpen) return;          // 输入命令时不搜索

        _ = SearchAsync(value);
    }

    /// <summary>选中一个前缀。</summary>
    public void SetPrefix(string prefix)
    {
        ActivePrefix = prefix;
        IsSlashOpen = false;

        // 清空搜索框但不触发查询
        _suppress = true;
        try { SearchText = ""; }
        finally { _suppress = false; }

        FootHint = $"已选择 {prefix}，请继续输入";
        _ = SearchAsync("");
    }

    private bool _suppress;

    /// <summary>执行斜杠命令（和主界面同一套行为）。</summary>
    public void RunSlash(SlashItemVM? item)
    {
        if (item is null) return;
        IsSlashOpen = false;

        _suppress = true;
        try { SearchText = ""; }
        finally { _suppress = false; }

        switch (item.Alias)
        {
            case "secret":
                ActiveKind = ClipKind.Secret;
                FootHint = "已筛选：只看凭据";
                _ = SearchAsync("");
                break;
            case "clear":
                ActiveKind = null;
                ActivePrefix = "";
                IsFilterBarOpen = false;
                FootHint = "已清空筛选";
                _ = SearchAsync("");
                break;
            case "tag":
                IsFilterBarOpen = true;
                FilterTab = "tag";
                _ = LoadFilterTagsAsync();
                FootHint = "选一个标签";
                break;
            case "type":
                IsFilterBarOpen = true;
                FilterTab = "type";
                LoadFilterKinds();
                FootHint = "选一个类型";
                break;
            case "from":
                IsFilterBarOpen = true;
                FilterTab = "from";
                _ = LoadFilterSourcesAsync();
                FootHint = "选一个来源";
                break;
            case "age":
                IsFilterBarOpen = true;
                FilterTab = "age";
                LoadFilterAges();
                FootHint = "选一个时间范围";
                break;
            case "ai":
                FootHint = "AI 搜索请在主界面用";
                break;
        }
    }

    /// <summary>当前生效的类型筛选。</summary>
    public ClipKind? ActiveKind { get; set; }

    // ───────── 快速筛选栏 ─────────

    [ObservableProperty] private bool _isFilterBarOpen;

    /// <summary>当前筛选页签：tag / type / from / age。</summary>
    [ObservableProperty] private string _filterTab = "";

    public ObservableCollection<FilterChoiceVM> FilterChoices { get; } = new();

    [RelayCommand]
    private void CloseFilterBar()
    {
        IsFilterBarOpen = false;
        FilterTab = "";
    }

    public void LoadFilterKindsPublic() => LoadFilterKinds();

    private void LoadFilterKinds()
    {
        FilterChoices.Clear();
        FilterChoices.Add(new FilterChoiceVM("type", "图片", "image", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "链接", "link", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "代码", "code", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "文件", "file", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "凭据", "secret", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "命令", "command", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "长文本", "longtext", 0));
        FilterChoices.Add(new FilterChoiceVM("type", "联系", "contact", 0));
    }

    private void LoadFilterAges()
    {
        FilterChoices.Clear();
        FilterChoices.Add(new FilterChoiceVM("age", "今天", "today", 0));
        FilterChoices.Add(new FilterChoiceVM("age", "最近 7 天", "7d", 0));
        FilterChoices.Add(new FilterChoiceVM("age", "最近 30 天", "30d", 0));
    }

    public async Task LoadFilterTagsAsync()
    {
        FilterChoices.Clear();
        var store = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                    as DittoShell.Domain.Abstractions.ITagStore;
        if (store is null) return;

        var all = await store.GetAllTagsAsync();
        foreach (var (name, count) in all.Where(t => t.Name != "__private")
            .OrderByDescending(t => t.Count).Take(30))
            FilterChoices.Add(new FilterChoiceVM("tag", "#" + name, name, count));
    }

    public async Task LoadFilterSourcesAsync()
    {
        FilterChoices.Clear();
        var sidecar = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.Sidecar.SidecarStore))
                      as DittoShell.Infrastructure.Sidecar.SidecarStore;
        if (sidecar is null) return;

        var dist = await sidecar.GetSourceDistributionAsync(30);
        foreach (var (app, count) in dist)
            FilterChoices.Add(new FilterChoiceVM("from", app, app, count));
    }

    /// <summary>选中一个筛选项。</summary>
    public void ApplyFilterChoice(FilterChoiceVM choice)
    {
        IsFilterBarOpen = false;

        switch (choice.Kind)
        {
            case "tag":
                FootHint = "标签筛选请在主界面用（快速面板暂不支持标签组合）";
                break;
            case "from":
                ActiveFrom = choice.Value;
                FootHint = "来源：" + choice.Value;
                _ = SearchAsync("");
                break;
            case "type":
                ActiveKind = choice.Value switch
                {
                    "image" => ClipKind.Image, "link" => ClipKind.Link,
                    "code" => ClipKind.Code, "file" => ClipKind.File,
                    "secret" => ClipKind.Secret, "command" => ClipKind.Command,
                    "longtext" => ClipKind.LongText, "contact" => ClipKind.Contact,
                    _ => (ClipKind?)null
                };
                FootHint = "类型：" + choice.Label;
                _ = SearchAsync("");
                break;
            case "age":
                ActiveAge = choice.Value;
                FootHint = "时间：" + choice.Label;
                _ = SearchAsync("");
                break;
        }
    }

    /// <summary>当前生效的来源筛选。</summary>
    public string? ActiveFrom { get; set; }

    /// <summary>当前生效的时间筛选。</summary>
    public string? ActiveAge { get; set; }


    private async Task SearchAsync(string q)
    {
        var raw = q.TrimStart('/');
        var query = ActivePrefix + raw;

        // tag: 前缀走标签查询
        if (ActivePrefix == "tag:" && raw.Trim().Length > 0)
        {
            var tagSvc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.TagService))
                         as DittoShell.Application.Services.TagService;

            if (tagSvc is not null)
            {
                var store = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                            as DittoShell.Domain.Abstractions.ITagStore;
                if (store is null) { FootHint = "标签存储未就绪"; return; }
                var hits = await store.GetClipIdsByTagsAsync(
                    raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(), false);
                var ids = hits.Select(h => h.ClipId).ToList();
                var map = ids.ToHashSet();
                var latest = await _reader.GetLatestAsync(800, null);
                var filtered = latest.Where(x => map.Contains(x.Id)).ToList();

                Items.Clear();
                foreach (var i in filtered) Items.Add(new ClipRowVM(i));
                NumberItems();
                Selected = Items.FirstOrDefault();
                FootHint = $"{Items.Count} 条 · tag:{raw}";
                return;
            }
        }

        var items = string.IsNullOrWhiteSpace(query)
            ? await _reader.GetLatestAsync(500, null)
            : await _reader.SearchAsync(query, null, ActiveKind);

        if (ActiveKind is not null)
            items = items.Where(x => x.Kind == ActiveKind.Value).ToList();

        // 来源筛选
        if (!string.IsNullOrWhiteSpace(ActiveFrom))
        {
            var sidecar = App.ServiceProvider?.GetService(
                typeof(DittoShell.Infrastructure.Sidecar.SidecarStore))
                as DittoShell.Infrastructure.Sidecar.SidecarStore;

            if (sidecar is not null)
            {
                var keep = new List<ClipItem>();
                foreach (var it in items)
                {
                    var src = await sidecar.GetSourceAsync(it.Id);
                    if (src is { } sv && sv.App is not null
                        && sv.App.Contains(ActiveFrom, StringComparison.OrdinalIgnoreCase))
                        keep.Add(it);
                }
                items = keep;
            }
        }

        // 时间筛选
        if (!string.IsNullOrWhiteSpace(ActiveAge))
        {
            var now = DateTimeOffset.Now;
            items = ActiveAge switch
            {
                "today" => items.Where(x => x.CreatedAt.ToLocalTime().Date == DateTime.Today).ToList(),
                "7d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 7).ToList(),
                "30d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 30).ToList(),
                _ => items
            };
        }

        Items.Clear();
        foreach (var i in items) Items.Add(new ClipRowVM(i));
        NumberItems();
        Selected = Items.FirstOrDefault();
    }

    private void NumberItems()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].IndexLabel = (i + 1).ToString();
            if (Items[i].IsImage) _ = Items[i].LoadThumbAsync(_reader, _thumbs, 96);
        }
    }

    /// <summary>切换隐私标记（不上传 AI）。</summary>
    public void TogglePrivate(ClipRowVM row)
    {
        row.IsPrivate = !row.IsPrivate;
        FootHint = row.IsPrivate
            ? $"已标记 #{row.Id} 为隐私（AI 不会读取）"
            : $"已取消 #{row.Id} 的隐私标记";

        _ = PersistPrivateAsync(row);
    }

    private async Task PersistPrivateAsync(ClipRowVM row)
    {
        if (_groups is null) return;
        try
        {
            // 复用 GroupService 之外的标签服务：直接走 TagService 更合适，这里用 sidecar 标签
            var tagSvc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.TagService))
                         as DittoShell.Application.Services.TagService;
            if (tagSvc is null) return;

            if (row.IsPrivate) await tagSvc.AddAsync(row.Id, "__private");
            else await tagSvc.RemoveAsync(row.Id, "__private");
        }
        catch { }
    }

    partial void OnSelectedChanged(ClipRowVM? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(ClipRowVM? row)
    {
        if (row is null) { DetailText = ""; return; }

        DetailText = row.Preview;
        DetailLoading = true;

        try
        {
            var detail = await _reader.GetDetailAsync(row.Id);
            if (detail is not null && Selected?.Id == row.Id)
                DetailText = string.IsNullOrWhiteSpace(detail.FullText) ? "(无文本内容)" : detail.FullText;
        }
        catch { }
        finally { DetailLoading = false; }
    }

    [RelayCommand]
    private void Manage() => RequestManage?.Invoke();

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void ToggleGroup() => IsGroupOpen = !IsGroupOpen;

    [RelayCommand]
    private void ToggleSlash()
    {
        IsSlashOpen = !IsSlashOpen;
        if (IsSlashOpen && !SearchText.StartsWith("/")) SearchText = "/";
    }

    [RelayCommand]
    private void PickSlash(SlashItemVM? item)
    {
        if (item is null) return;
        SearchText = item.Alias;
        IsSlashOpen = false;
    }

    [RelayCommand]
    private void SelectGroup(GroupRowVM? g)
    {
        if (g is null) return;
        foreach (var x in Groups) x.IsSelected = false;
        g.IsSelected = true;
        IsGroupOpen = false;
    }
}
