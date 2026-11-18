using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DittoShell.App.Localization;
using DittoShell.App.Media;
using DittoShell.App.Services;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.App.ViewModels;

/// <summary>列表行。所有显示用的派生值在这里算好，避免 XAML 里堆转换器。</summary>
public sealed partial class ClipRowVM : ObservableObject
{
    /// <summary>全局共享的敏感检测器（列表项构造时用）。</summary>
    public static readonly Lazy<DittoShell.Application.Services.SensitiveDetector> Detector =
        new(() => new DittoShell.Application.Services.SensitiveDetector());

    /// <summary>用户手动标记的隐私条目（不上传 AI）。</summary>
    [ObservableProperty] private bool _isPrivate;

    /// <summary>隐私条目是否显示原文（默认打码）。</summary>
    [ObservableProperty] private bool _showRawText;

    /// <summary>隐私条目的预览文本（打码 or 原文，随 ShowRawText 切换）。</summary>
    [ObservableProperty] private string _previewMasked = "";
    [ObservableProperty] private string _previewRaw = "";

    /// <summary>当前应显示的预览（隐私+未解锁 → 打码）。</summary>
    public string DisplayPreview => IsPrivate && !ShowRawText ? PreviewMasked : PreviewRaw;

    partial void OnShowRawTextChanged(bool value) => OnPropertyChanged(nameof(DisplayPreview));

    /// <summary>
    /// 部分打码：保留前 10 和后 6 个字符，中间用 · 替代。
    /// 和详情页的部分打码视觉一致（能看出是什么，但看不到完整内容）。
    /// </summary>
    internal static string PartialMask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (t.Length == 0) return t;

        // 很短：保留首 2 尾 2（至少各 1）
        if (t.Length <= 6)
        {
            if (t.Length <= 2) return new string('·', t.Length);
            var h0 = Math.Min(2, t.Length / 2);
            var t0 = Math.Min(2, t.Length - h0);
            return t[..h0] + new string('·', Math.Max(1, t.Length - h0 - t0)) + t[^t0..];
        }

        // 中等：保留首 4 尾 3
        if (t.Length <= 16)
        {
            const int h1 = 4, t1 = 3;
            return t[..h1] + new string('·', t.Length - h1 - t1) + t[^t1..];
        }

        // 正常：保留首 10 尾 6，中间最多 24 个点
        const int head = 10, tail = 6;
        var mid = Math.Min(t.Length - head - tail, 24);
        return t[..head] + new string('·', mid) + t[^tail..];
    }
    partial void OnIsPrivateChanged(bool value) => OnPropertyChanged(nameof(DisplayPreview));

    /// <summary>原始数据（AI 重排等场景需要）。</summary>
    public ClipItem Source { get; }

    public ClipRowVM(ClipItem item, bool isPrivate = false)
    {
        Source = item;
        Id = item.Id;
        IsPrivate = isPrivate;
        var preview = item.Preview.Replace("\r", " ").Replace("\n", " ").Replace("\t", "  ").Trim();
        IsImage = item.Kind == ClipKind.Image;

        // Ditto 对图片条目只存 "CF_DIB" 这种格式名，换成可读的说明
        if (IsImage && (preview.Length == 0 || preview.Equals("CF_DIB", StringComparison.OrdinalIgnoreCase)))
            preview = $"图片 · {item.CreatedAt:MM-dd HH:mm}";

        // 敏感内容在列表里打码（凭据、私钥、身份证等）
        var hits = Detector.Value.Detect(preview);
        if (hits.Count > 0)
        {
            preview = Detector.Value.Mask(preview);
            IsSensitive = true;
        }

        // 隐私条目：存原文 + 部分打码版（保留头尾，让人能看出是什么）
        PreviewRaw = preview;

        if (isPrivate && preview.Length > 0)
        {
            PreviewMasked = PartialMask(preview);
            preview = PreviewMasked;
            IsSensitive = true;
        }
        else
        {
            PreviewMasked = preview;
        }

        Preview = preview;
        IsSensitive = IsSensitive || item.Kind == ClipKind.Secret;
        CharCount = item.CharCount;
        CreatedAt = item.CreatedAt;
        Kind = item.Kind;

        (KindLabel, KindIcon) = item.Kind switch
        {
            ClipKind.Link => (L.T("kind.link"), "Link"),
            ClipKind.Code => (L.T("kind.code"), "Code"),
            ClipKind.Image => (L.T("kind.image"), "Image"),
            ClipKind.File => (L.T("kind.file"), "File"),
            ClipKind.Secret => (L.T("kind.secret"), "Key"),
            ClipKind.Unknown => (L.T("kind.text"), "File"),
            _ => (L.T("kind.text"), "Type")
        };

        KindBrush = item.Kind switch
        {
            ClipKind.Link => Find("B.Cyan"),
            ClipKind.Code => Find("B.Purple"),
            ClipKind.Image => Find("B.Magenta"),
            ClipKind.File => Find("B.Warning"),
            ClipKind.Secret => Find("B.Danger"),
            _ => Find("B.Primary")
        };

        TimeText = FormatTime(item.CreatedAt);
        MetaText = IsImage
            ? L.T("kind.image")
            : item.CharCount > 200
                ? $"{L.T("kind.longText")} · {item.CharCount} 字"
                : item.CharCount > 0
                    ? $"{KindLabel} · {item.CharCount} 字"
                    : KindLabel;
    }

    public long Id { get; }
    public string Preview { get; }
    public ClipKind Kind { get; }
    public string KindLabel { get; }
    public string KindIcon { get; }
    public Brush KindBrush { get; }
    public string TimeText { get; }
    public string MetaText { get; }
    public bool IsImage { get; }
    public bool IsSensitive { get; }
    public int CharCount { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>快速面板里的序号（1-10），由填充方设置。</summary>
    public string IndexLabel { get; set; } = "";

    /// <summary>图片缩略图（异步加载，加载完通知界面）。</summary>
    [ObservableProperty] private BitmapSource? _thumb;
    [ObservableProperty] private bool _thumbLoaded;

    /// <summary>加载缩略图：先查内存缓存，未命中则从库里取 CF_DIB 解码。</summary>
    public async Task LoadThumbAsync(IClipReader reader, ThumbnailCache cache, int decodeWidth = 200, CancellationToken ct = default)
    {
        if (!IsImage || ThumbLoaded) return;

        if (cache.TryGet(Id, out var cached) && cached is not null)
        {
            Thumb = cached;
            ThumbLoaded = true;
            return;
        }

        try
        {
            var dib = await reader.GetRawFormatAsync(Id, "CF_DIB", ct);
            if (dib is null) { ThumbLoaded = true; return; }

            var bmp = await Task.Run(() => DibDecoder.Decode(dib, decodeWidth), ct);
            if (bmp is not null)
            {
                cache.Put(Id, bmp);
                Thumb = bmp;
            }
        }
        catch
        {
            // 解码失败只影响缩略图，不影响列表
        }
        finally
        {
            ThumbLoaded = true;
        }
    }

    public void ApplyIndex(int i) => IndexLabel = (i + 1).ToString();

    private static Brush Find(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static string FormatTime(DateTimeOffset t)
    {
        var now = DateTimeOffset.Now;
        var d = now - t;
        if (d.TotalSeconds < 0) return t.ToString("MM-dd");
        if (d.TotalSeconds < 60) return "刚刚";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Date == now.Date.AddDays(-1)) return L.T("common.yesterday");
        return t.ToString("MM-dd");
    }
}

/// <summary>侧栏一行（真实分组或虚拟视图）。</summary>
public sealed partial class GroupRowVM : ObservableObject
{
    public GroupRowVM(long id, string name, int count, string icon, int indent = 0, bool hasChildren = false)
    {
        Id = id; Name = name; Count = count; Icon = icon; Indent = indent; HasChildren = hasChildren;
    }

    public long Id { get; }
    public string Name { get; }
    public int Count { get; }
    public string Icon { get; }
    public int Indent { get; }
    public bool HasChildren { get; }

    /// <summary>父分组 ID（0 = 顶层）。</summary>
    public long ParentId { get; set; }

    /// <summary>层级深度（0 = 顶层）。</summary>
    public int Depth { get; set; }

    /// <summary>连接线前缀（├── / └── / 带竖线的 │  ├──）。</summary>
    public string Connector { get; set; } = "";

    /// <summary>是否是所在层的最后一个（末位用 └）。</summary>
    public bool IsLastChild { get; set; }

    /// <summary>缩进宽度（每级 14px）。</summary>
    public double IndentWidth => Depth * 14;

    /// <summary>组名字号：顶层 13，越深越小。</summary>
    public double NameFontSize => Depth switch
    {
        0 => 13,
        1 => 12,
        _ => 11.5
    };

    /// <summary>组名字重：顶层加粗。</summary>
    public System.Windows.FontWeight NameWeight => Depth == 0
        ? System.Windows.FontWeights.SemiBold
        : System.Windows.FontWeights.Normal;

    /// <summary>组名颜色：越深越浅。</summary>
    public string NameColorKey => Depth switch
    {
        0 => "B.Text",
        1 => "B.Text2",
        _ => "B.Text3"
    };

    /// <summary>连接线是否可见（顶层不画）。</summary>
    public bool ShowConnector => Depth > 0;

    /// <summary>是否显示折叠箭头（有子组才显示）。</summary>
    public bool ShowArrow => HasChildren;

    /// <summary>层级缩进文本（树形显示用）。</summary>
    public string IndentText { get; set; } = "";

    /// <summary>分支符号（└ 或 ├）。</summary>
    public string BranchMarker { get; set; } = "";

    /// <summary>拖动时是否作为放置目标。</summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>放置位置：0=无 1=上方插入 2=下方插入 3=移入内部。</summary>
    [ObservableProperty] private int _dropPosition;

    public bool ShowInsertTop => IsDropTarget && DropPosition == 1;
    public bool ShowInsertBottom => IsDropTarget && DropPosition == 2;
    public bool ShowDropInto => IsDropTarget && DropPosition == 3;

    partial void OnIsDropTargetChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInsertTop));
        OnPropertyChanged(nameof(ShowInsertBottom));
        OnPropertyChanged(nameof(ShowDropInto));
    }

    partial void OnDropPositionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowInsertTop));
        OnPropertyChanged(nameof(ShowInsertBottom));
        OnPropertyChanged(nameof(ShowDropInto));
    }

    /// <summary>真实分组（Id>0），可以改名/删除/展开。</summary>
    public bool IsRealGroup => Id > 0;

    private static readonly string[] DotColors =
    {
        "#FF9C27B0", "#FF1677FF", "#FF52C41A", "#FFFA8C16",
        "#FFEB2F96", "#FF13C2C2", "#FF722ED1", "#FFF5222D"
    };

    /// <summary>分组圆点颜色（按 Id 固定分配，同一分组永远同色）。</summary>
    public System.Windows.Media.Brush DotBrush
    {
        get
        {
            var idx = (int)(Math.Abs(Id) % DotColors.Length);
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(DotColors[idx]));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>箭头是否显示：真实分组都显示（没子组点了会提示）。</summary>
    public Thickness Padding => new(10 + Indent * 16, 0, 10, 0);

    /// <summary>父组是否展开（子组是否显示）。</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>折叠箭头方向。</summary>
    public string ArrowGlyph => HasChildren ? (IsExpanded ? "ChevronDown" : "ChevronRight") : "";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ArrowGlyph));

    [ObservableProperty] private bool _isSelected;
}

public enum SortMode { TimeDesc, TimeAsc, LongDesc, ShortAsc, ByApp, ByGroup, ByType, ByPaste, ByRecentUse, ByAge }

public enum ShellPage { Clips, Settings, Stats }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IClipReader _reader;
    private readonly ThumbnailCache _thumbs;
    private readonly IDittoBridge? _bridge;
    private readonly DittoShell.Application.Services.TagService? _tags;
    private readonly DittoShell.Application.Services.AiService? _ai;
    private readonly DittoShell.Application.Services.SourceTrackingService? _source;
    private readonly DittoShell.Infrastructure.DittoDb.DittoDbWriter? _writerForEdit;
    private bool _suppressSearch;
    private readonly DittoShell.Domain.Abstractions.ITagStore? _tagStore;
    private readonly DittoShell.Domain.Abstractions.ISourceStore? _sourceStore;

    public MainViewModel(IClipReader reader, ThumbnailCache thumbs, IDittoBridge? bridge = null,
                         DittoShell.Application.Services.TagService? tags = null,
                         DittoShell.Application.Services.AiService? ai = null,
                         DittoShell.Application.Services.SourceTrackingService? source = null,
                         DittoShell.Infrastructure.DittoDb.DittoDbWriter? writerForEdit = null,
                         DittoShell.Domain.Abstractions.ITagStore? tagStore = null)
    {
        _reader = reader;
        _thumbs = thumbs;
        _bridge = bridge;
        _tags = tags;
        _ai = ai;

        AiChat = new AiChatViewModel(
            App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.AiAgentService))
            as DittoShell.Application.Services.AiAgentService,
            App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.Sidecar.AiSessionStore))
            as DittoShell.Infrastructure.Sidecar.AiSessionStore);

        // AI 写完数据后刷新主界面（分组树 + 当前列表）
        AiChat.RequestRefresh += async () =>
        {
            App.NotifyGroupsChanged();
            await LoadAsync();
            StatusText = "AI 已修改数据，界面已刷新";
        };
        _source = source;
        _writerForEdit = writerForEdit;
        _tagStore = tagStore;
        _sourceStore = App.ServiceProvider?.GetService(
            typeof(DittoShell.Domain.Abstractions.ISourceStore))
            as DittoShell.Domain.Abstractions.ISourceStore;

        RowHeight = App.Config.ListDensity is >= 40 and <= 96 ? App.Config.ListDensity : 70;
        App.DensityChanged += v => RowHeight = v;
        App.SidebarChanged += () => _ = LoadAsync();

        FilterTags.CollectionChanged += (_, _) => NotifyFiltersChanged();
        _ = LoadAsync();
    }

    /// <summary>AI 会话（右侧抽屉）。</summary>
    public AiChatViewModel AiChat { get; }

    /// <summary>请求打开 AI 抽屉（可带初始问题）。</summary>
    public event Action<string>? RequestOpenAiDrawer;

    public ObservableCollection<ClipRowVM> Items { get; } = new();

    /// <summary>多选中的条目（含当前选中项）。</summary>
    public ObservableCollection<ClipRowVM> SelectedClips { get; } = new();

    public int SelectionCount => SelectedClips.Count;
    public bool HasMultiSelection => SelectedClips.Count > 1;

    /// <summary>由 View 的 SelectionChanged 调用。</summary>
    public void SyncSelection(System.Collections.IList selected)
    {
        SelectedClips.Clear();
        foreach (var s in selected)
            if (s is ClipRowVM row) SelectedClips.Add(row);

        if (SelectedClips.Count == 1) Selected = SelectedClips[0];

        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>当前选中的分组（用于新建子分组）。</summary>
    public GroupRowVM? SelectedGroupRow { get; set; }

    /// <summary>选中的 Id 快照（供批量操作读）。</summary>
    public List<long> SelectedIdSnapshot()
        => SelectedClips.Select(x => x.Id).ToList();

    /// <summary>选中数变化时通知 UI。</summary>
    public void NotifySelectionCountChanged()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>取要操作的目标（多选时用集合，否则用当前项）。</summary>
    public IReadOnlyList<long> TargetIds()
    {
        if (SelectedClips.Count > 0) return SelectedClips.Select(x => x.Id).ToList();
        return Selected is null ? Array.Empty<long>() : new[] { Selected.Id };
    }
    public ObservableCollection<GroupRowVM> Views { get; } = new();
    public ObservableCollection<GroupRowVM> Groups { get; } = new();
    public ObservableCollection<string> PreviewTags { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ClipRowVM? _selected;
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _previewHasSecret;

    /// <summary>这条内容有没有 HTML 格式（决定是否显示「富文本」切换）。</summary>
    [ObservableProperty] private bool _previewHasRichText;

    /// <summary>当前是否以富文本显示。</summary>
    [ObservableProperty] private bool _previewIsRich;

    /// <summary>HTML 片段（CF_HTML 解析后）。</summary>
    [ObservableProperty] private string _previewHtml = "";
    [ObservableProperty] private bool _previewShowRaw;

    /// <summary>切换预览是否显示原文（默认打码）。</summary>
    /// <summary>保存编辑：直接写回 Ditto 库（原条目）。</summary>
    [RelayCommand]
    private async Task SaveEdited()
    {
        if (_writerForEdit is null) { StatusText = "写入通道未就绪"; return; }
        if (PreviewId <= 0) { StatusText = "没有选中条目"; return; }

        var text = PreviewText ?? "";
        if (string.IsNullOrEmpty(text)) { StatusText = "内容为空，不保存"; return; }

        StatusText = "正在保存（会先备份数据库）…";
        try
        {
            await _writerForEdit.UpdateTextAsync(PreviewId, text);
            _rawPreviewText = text;
            PreviewHasSecret = ClipRowVM.Detector.Value.HasSensitive(text);
            PreviewText = PreviewShowRaw ? text : MaskIfNeeded(text);
            StatusText = $"已保存到条目 #{PreviewId}（原条目已更新，可撤销性靠 backup/）";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
    }

    /// <summary>另存为新条目：把文本放剪贴板，让 Ditto 自己录一条。</summary>
    [RelayCommand]
    private void SaveAsNew()
    {
        var text = PreviewText ?? "";
        if (string.IsNullOrEmpty(text)) { StatusText = "内容为空"; return; }

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "已复制为新的剪贴板内容（Ditto 会在 1 秒内收录为新条目）";
        }
        catch (Exception ex)
        {
            StatusText = "另存失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void ToggleRich()
    {
        PreviewIsRich = !PreviewIsRich;
    }

    [RelayCommand]
    private void TogglePreviewRaw()
    {
        PreviewShowRaw = !PreviewShowRaw;
        ReloadPreviewText();
    }

    private string _rawPreviewText = "";

    private void ReloadPreviewText()
    {
        PreviewText = PreviewShowRaw ? _rawPreviewText : MaskIfNeeded(_rawPreviewText);
    }

    private static string MaskIfNeeded(string text)
    {
        var hits = ClipRowVM.Detector.Value.Detect(text);
        return hits.Count == 0 ? text : ClipRowVM.Detector.Value.Mask(text);
    }
    [ObservableProperty] private string _previewFormats = "";
    [ObservableProperty] private string _previewSize = "";
    [ObservableProperty] private string _previewGroup = "";
    [ObservableProperty] private string _previewPasteCount = "";
    [ObservableProperty] private string _previewSource = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _dbPath = "";
    [ObservableProperty] private long _loadMs;
    [ObservableProperty] private string _previewKindLabel = "";
    [ObservableProperty] private Brush _previewKindBrush = Brushes.Gray;
    [ObservableProperty] private string _previewTime = "";
    [ObservableProperty] private long _previewId;
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private bool _previewIsImage;
    [ObservableProperty] private string _aiSummary = "";
    [ObservableProperty] private bool _aiBusy;

    [ObservableProperty] private double _rowHeight = 70;

    [ObservableProperty] private ShellPage _currentPage = ShellPage.Clips;

    // ───────── 左侧视图选择 ─────────

    [ObservableProperty] private GroupRowVM? _selectedView;
    [ObservableProperty] private bool _sortDescending = true;

    /// <summary>排序方式。</summary>
    [ObservableProperty] private SortMode _sortMode = SortMode.TimeDesc;

    [RelayCommand]
    private async Task SetSort(SortMode mode)
    {
        SortMode = mode;
        SortDescending = mode is SortMode.TimeDesc or SortMode.LongDesc;
        await ReloadForViewAsync();
        StatusText = $"排序：{SortModeLabel(mode)}";
    }

    public string SortModeLabel(SortMode m) => m switch
    {
        SortMode.TimeDesc => "最新优先",
        SortMode.TimeAsc => "最早优先",
        SortMode.LongDesc => "最长优先",
        SortMode.ShortAsc => "最短优先",
        SortMode.ByApp => "按来源应用",
        SortMode.ByGroup => "按分组",
        SortMode.ByType => "按类型",
        SortMode.ByPaste => "粘贴次数",
        SortMode.ByRecentUse => "最近使用",
        SortMode.ByAge => "最久未用",
        _ => "默认"
    };
    [ObservableProperty] private bool _isCommandPanelOpen;

    /// <summary>斜杠命令面板的候选项（按输入前缀过滤）。</summary>
    public ObservableCollection<SlashCommandVM> Commands { get; } = new();

    private readonly List<SlashCommandVM> _allCommands = new();

    /// <summary>最近一次筛选后的视图类型（用于搜索时保持一致）。</summary>
    private long _activeViewId = -1;
    private List<Domain.Models.GroupNode> _groupTree = new();

    /// <summary>隐私条目 ID 集合（加载时刷新，用于列表打码）。</summary>
    private HashSet<long> _privateIds = new();

    [ObservableProperty] private string _groupSummary = "分组";

    // ───────── 标签筛选 ─────────

    /// <summary>左侧可选的标签（含使用次数）。</summary>
    public ObservableCollection<TagChipVM> TagList { get; } = new();

    // ───────── 搜索前缀（tag: / kind: / from:） ─────────

    /// <summary>已选中的前缀，例如 "tag:"。为空表示普通搜索。</summary>
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
        StatusText = "前缀已清除";
    }

    // ───────── 筛选栏（新搜索区） ─────────

    /// <summary>当前选中的标签条件。</summary>
    public ObservableCollection<string> FilterTags { get; } = new();

    [ObservableProperty] private string _filterKindLabel = "";
    [ObservableProperty] private string _filterFromLabel = "";
    [ObservableProperty] private string _filterAgeLabel = "";

    private ClipKind? _filterKind;
    private string? _filterFrom;
    private string? _filterAge;

    /// <summary>筛选栏是否可见。</summary>
    public bool HasFilters =>
        FilterTags.Count > 0 || _filterKind is not null
        || _filterFrom is not null || _filterAge is not null;

    /// <summary>筛选栏摘要文案。</summary>
    public string FilterSummary
    {
        get
        {
            var parts = new List<string>();
            if (FilterTags.Count > 0)
                parts.Add("标签 " + string.Join(" ", FilterTags.Select(t => "#" + t)));
            if (_filterKind is not null) parts.Add(FilterKindLabel);
            if (_filterFrom is not null) parts.Add(FilterFromLabel);
            if (_filterAge is not null) parts.Add(FilterAgeLabel);
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>自动补全候选。</summary>
    public ObservableCollection<SearchSuggestVM> Suggestions { get; } = new();
    [ObservableProperty] private bool _isSuggestOpen;

    /// <summary>搜索历史。</summary>
    public ObservableCollection<RecentSearchVM> RecentSearches { get; } = new();
    [ObservableProperty] private bool _isRecentOpen;

    /// <summary>历史为空时的提示。</summary>
    public bool RecentIsEmpty => RecentSearches.Count == 0;

    /// <summary>筛选面板开关。</summary>
    [ObservableProperty] private bool _isFilterPanelOpen;

    [RelayCommand]
    public void AddFilterTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || FilterTags.Contains(tag)) return;
        FilterTags.Add(tag);
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    [RelayCommand]
    private void RemoveFilterTag(string tag)
    {
        FilterTags.Remove(tag);
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    public void SetFilterKind(ClipKind? kind, string label)
    {
        _filterKind = kind;
        FilterKindLabel = kind is null ? "" : label;
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    public void SetFilterFrom(string? from)
    {
        _filterFrom = from;
        FilterFromLabel = from is null ? "" : "来源 " + from;
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    public void SetFilterAge(string? age, string label)
    {
        _filterAge = age;
        FilterAgeLabel = age is null ? "" : label;
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    [RelayCommand]
    private void ClearAllFilters()
    {
        FilterTags.Clear();
        _filterKind = null;
        _filterFrom = null;
        _filterAge = null;
        FilterKindLabel = "";
        FilterFromLabel = "";
        FilterAgeLabel = "";
        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
        StatusText = "已清空全部筛选";
    }

    private void NotifyFiltersChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(FilterSummary));
    }

    /// <summary>根据输入生成候选。</summary>
    public async Task UpdateSuggestionsAsync(string raw)
    {
        var text = (raw ?? "").TrimStart();

        if (text.Length == 0)
        {
            IsSuggestOpen = false;
            Suggestions.Clear();
            return;
        }

        string? prefix = null;
        var keyword = "";
        foreach (var pf in new[] { "tag:", "type:", "from:", "age:" })
        {
            if (!text.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) continue;
            prefix = pf;
            keyword = text[pf.Length..].Trim();
            break;
        }

        if (prefix is null) { IsSuggestOpen = false; Suggestions.Clear(); return; }

        Suggestions.Clear();

        if (prefix == "tag:")
        {
            if (_tagStore is not null)
            {
                var all = await _tagStore.GetAllTagsAsync();
                foreach (var (name, count) in all
                    .Where(t => t.Name != "__private"
                        && (keyword.Length == 0
                            || t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(t => t.Count)
                    .Take(8))
                {
                    Suggestions.Add(new SearchSuggestVM("tag:", name, "#" + name, count, "Tag"));
                }
            }
        }
        else if (prefix == "type:")
        {
            var kinds = new (string Key, string Label, string Icon)[]
            {
                ("image", "图片", "Image"),
                ("link", "链接", "Link"),
                ("code", "代码", "Code"),
                ("file", "文件", "FileText"),
                ("secret", "凭据", "Shield"),
                ("command", "命令", "Terminal"),
                ("longtext", "长文本", "AlignLeft"),
                ("contact", "联系", "Mail"),
            };
            foreach (var k in kinds.Where(k =>
                keyword.Length == 0
                || k.Label.Contains(keyword)
                || k.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                Suggestions.Add(new SearchSuggestVM("type:", k.Key, k.Label, 0, k.Icon));
            }
        }
        else if (prefix == "from:")
        {
            var sidecar = App.ServiceProvider?.GetService(
                typeof(DittoShell.Infrastructure.Sidecar.SidecarStore))
                as DittoShell.Infrastructure.Sidecar.SidecarStore;

            if (sidecar is not null)
            {
                var dist = await sidecar.GetSourceDistributionAsync(20);
                foreach (var (app, count) in dist
                    .Where(d => keyword.Length == 0
                        || d.App.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Count)
                    .Take(8))
                {
                    Suggestions.Add(new SearchSuggestVM("from:", app, app, count, "ExternalLink"));
                }
            }
        }
        else if (prefix == "age:")
        {
            var opts = new[]
            {
                ("today", "今天"),
                ("7d", "最近 7 天"),
                ("30d", "最近 30 天"),
            };
            foreach (var (key, label) in opts.Where(o =>
                keyword.Length == 0 || o.Item2.Contains(keyword)))
                Suggestions.Add(new SearchSuggestVM("age:", key, label, 0, "Calendar"));
        }

        IsSuggestOpen = Suggestions.Count > 0;
        if (Suggestions.Count > 0) Suggestions[0].IsHighlighted = true;
    }

    /// <summary>选中一条候选。</summary>
    public void AcceptSuggestion(SearchSuggestVM s)
    {
        IsSuggestOpen = false;

        if (s.Prefix == "tag:")
        {
            AddFilterTag(s.Value);
        }
        else if (s.Prefix == "type:")
        {
            var kind = s.Value switch
            {
                "image" => ClipKind.Image,
                "link" => ClipKind.Link,
                "code" => ClipKind.Code,
                "file" => ClipKind.File,
                "secret" => ClipKind.Secret,
                "command" => ClipKind.Command,
                "longtext" => ClipKind.LongText,
                "contact" => ClipKind.Contact,
                _ => (ClipKind?)null
            };
            SetFilterKind(kind, s.Label);
        }
        else if (s.Prefix == "from:")
        {
            SetFilterFrom(s.Value);
        }
        else if (s.Prefix == "age:")
        {
            SetFilterAge(s.Value, s.Label);
        }

        _suppressSearch = true;
        try { SearchText = ""; }
        finally { _suppressSearch = false; }

        StatusText = "已添加筛选条件";
    }

    public void LoadRecentSearches()
    {
        RecentSearches.Clear();
        var cfg = App.Config.RecentSearches ?? new List<RecentSearch>();
        foreach (var r in cfg.OrderByDescending(x => x.At).Take(10))
            RecentSearches.Add(new RecentSearchVM(r.Text, r.Hits));

        OnPropertyChanged(nameof(RecentIsEmpty));
    }

    public void PushRecentSearch()
    {
        var text = (SearchText ?? "").Trim();
        if (text.Length == 0 && FilterTags.Count == 0) return;

        var desc = text;
        if (FilterTags.Count > 0)
            desc = string.Join(" ", FilterTags.Select(t => "tag:" + t)) + " " + text;
        desc = desc.Trim();

        var list = App.Config.RecentSearches ??= new List<RecentSearch>();
        list.RemoveAll(x => x.Text == desc);
        list.Insert(0, new RecentSearch
        {
            Text = desc,
            Tags = FilterTags.ToList(),
            Kind = _filterKind?.ToString(),
            From = _filterFrom,
            Hits = Items.Count,
            At = DateTimeOffset.Now.ToString("O")
        });
        if (list.Count > 10) list.RemoveRange(10, list.Count - 10);

        App.SaveConfig();
        LoadRecentSearches();
    }

    public void AcceptRecent(RecentSearchVM r)
    {
        IsRecentOpen = false;
        FilterTags.Clear();

        var cfg = App.Config.RecentSearches ?? new List<RecentSearch>();
        var hit = cfg.FirstOrDefault(x => x.Text == r.Text);
        if (hit?.Tags is not null)
            foreach (var t in hit.Tags) FilterTags.Add(t);

        NotifyFiltersChanged();
        _ = ReloadForViewAsync();
    }

    /// <summary>把筛选条件解析成查询参数。</summary>
    private (string? tags, ClipKind? kind, string? from, string text) ParseChips(string raw)
        => (FilterTags.Count > 0 ? string.Join(" ", FilterTags) : null,
            _filterKind,
            _filterFrom,
            (raw ?? "").Trim());

    // ───────── 查询解释（Explain）─────────

    [ObservableProperty] private bool _isExplainOpen;
    [ObservableProperty] private string _explainText = "";

    [RelayCommand]
    private void ToggleExplain()
    {
        if (IsExplainOpen) { IsExplainOpen = false; return; }
        ExplainText = BuildExplanation();
        IsExplainOpen = true;
    }

    /// <summary>把当前查询翻译成人话。</summary>
    private string BuildExplanation()
    {
        var lines = new List<string>();
        var raw = (SearchText ?? "").Trim();

        foreach (var tag in FilterTags)
            lines.Add($"· 标签包含「#{tag}」");

        if (_filterKind is not null)
            lines.Add($"· 类型是「{FilterKindLabel}」");

        if (_filterFrom is not null)
            lines.Add($"· 来源程序是「{_filterFrom}」");

        if (_filterAge is not null)
            lines.Add($"· 时间范围「{FilterAgeLabel}」");

        if (raw.Length > 0)
            lines.Add($"· 全文包含「{raw}」");

        if (lines.Count == 0)
            lines.Add("· 没有筛选条件，显示全部");

        lines.Add("");
        lines.Add($"→ 当前结果 {Items.Count} 条");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把 chips 解析成查询参数。</summary>
    

    /// <summary>当前选中的标签。</summary>
    public ObservableCollection<string> SelectedTags { get; } = new();

    /// <summary>严格模式：只显示含全部选中标签的；关闭则按命中数排序。</summary>
    [ObservableProperty] private bool _strictTagMode;

    /// <summary>标签浮层是否打开。</summary>
    [ObservableProperty] private bool _isTagPanelOpen;

    /// <summary>标签按钮上的文案。</summary>
    public string TagButtonText => SelectedTags.Count == 0 ? "标签" : $"已选 {SelectedTags.Count}";

    /// <summary>浮层里的标签搜索词。</summary>
    [ObservableProperty] private string _tagSearchText = "";

    partial void OnTagSearchTextChanged(string value) => RefreshTagListFilter();

    private void RefreshTagListFilter()
    {
        var key = (TagSearchText ?? "").Trim();

        TagList.Clear();
        foreach (var (name, count) in _allTags)
        {
            if (name == "__private") continue;
            if (key.Length > 0 && !name.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
            TagList.Add(new TagChipVM(name, count) { IsSelected = SelectedTags.Contains(name) });
        }
    }

    private List<(string Name, int Count)> _allTags = new();

    [RelayCommand]
    private void ToggleTagPanel() => IsTagPanelOpen = !IsTagPanelOpen;

    [RelayCommand]
    private void CloseTagPanel() => IsTagPanelOpen = false;

    partial void OnStrictTagModeChanged(bool value) => _ = ApplyTagFilterAsync();

    [RelayCommand]
    private async Task ToggleTag(TagChipVM? chip)
    {
        if (chip is null) return;
        chip.IsSelected = !chip.IsSelected;

        if (chip.IsSelected) { if (!SelectedTags.Contains(chip.Name)) SelectedTags.Add(chip.Name); }
        else SelectedTags.Remove(chip.Name);

        OnPropertyChanged(nameof(TagButtonText));
        await ApplyTagFilterAsync();
    }

    [RelayCommand]
    private void ClearTags()
    {
        foreach (var t in TagList) t.IsSelected = false;
        SelectedTags.Clear();
        OnPropertyChanged(nameof(TagButtonText));
        _ = ApplyTagFilterAsync();
    }

    private async Task ApplyTagFilterAsync()
    {
        if (SelectedTags.Count == 0)
        {
            await ReloadForViewAsync();
            StatusText = "标签筛选已清空";
            return;
        }

        if (_tagStore is null) { StatusText = "标签存储未就绪"; return; }

        var hits = await _tagStore.GetClipIdsByTagsAsync(SelectedTags.ToList(), StrictTagMode);
        if (hits.Count == 0)
        {
            Items.Clear();
            StatusText = $"没有条目含这些标签（{(StrictTagMode ? "严格" : "相关")}）";
            return;
        }

        var map = hits.Select(h => h.ClipId).ToHashSet();
        var all = await _reader.GetLatestAsync(800, null);
        var ordered = all.Where(x => map.Contains(x.Id)).ToList();

        Items.Clear();
        foreach (var i in ordered) Items.Add(new ClipRowVM(i, _privateIds.Contains(i.Id)));

        Selected = Items.FirstOrDefault();
        StatusText = $"{Items.Count} 条 · 标签 {(StrictTagMode ? "严格匹配" : "按相关度")}";
    }

    private async Task LoadTagsAsync()
    {
        if (_tagStore is null) return;
        try
        {
            _allTags = await _tagStore.GetAllTagsAsync();
            RefreshTagListFilter();
        }
        catch { }
    }

    /// <summary>左侧「筛选分组名」，只影响分组列表，不影响历史搜索。</summary>
    [ObservableProperty] private string _groupFilter = "";

    partial void OnGroupFilterChanged(string value) => RebuildGroups();

    partial void OnSelectedViewChanged(GroupRowVM? value)
    {
        if (value is null) return;

        foreach (var v in Views) v.IsSelected = v.Id == value.Id;
        foreach (var g in Groups) g.IsSelected = g.Id == value.Id;

        _activeViewId = value.Id;
        Title = value.Name;
        _ = ReloadForViewAsync();
    }

    private async Task ReloadForViewAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText) && !SearchText.StartsWith('/'))
        {
            await SearchAsync();
            return;
        }

        List<ClipItem> items;

        if (_activeViewId == -2)
        {
            // 今天：取最近 800 条再按日期过滤
            var raw = await _reader.GetLatestAsync(800, null);
            items = raw.Where(x => x.CreatedAt.ToLocalTime().Date == DateTime.Today).ToList();
        }
        else
        {
            var (groupId, viewKind) = ResolveView(_activeViewId);
            var (chipTags, chipKind, chipFrom, _) = ParseChips("");

            if (_activeViewId == -12)
            {
                // 隐私：用 __private 标签筛
                var privIds = await _tagStore.GetClipIdsByTagsAsync(
                    new List<string> { "__private" }, false);
                var idSet = privIds.Select(x => x.ClipId).ToHashSet();
                var all = await _reader.GetLatestAsync(3000, null);
                items = all.Where(x => idSet.Contains(x.Id)).ToList();
            }
            else
            {
                items = (await _reader.SearchAsync(
                    "",
                    groupId,
                    chipKind ?? viewKind,
                    chipTags,
                    chipFrom)).ToList();
            }
        }

        // 时间范围筛选
        if (_filterAge is not null)
        {
            var now = DateTimeOffset.Now;
            items = _filterAge switch
            {
                "today" => items.Where(x => x.CreatedAt.ToLocalTime().Date == DateTime.Today).ToList(),
                "7d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 7).ToList(),
                "30d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 30).ToList(),
                _ => items
            };
        }

        var ordered = SortMode switch
        {
            SortMode.TimeAsc => items.OrderBy(x => x.CreatedAt).ToList(),
            SortMode.LongDesc => items.OrderByDescending(x => x.CharCount).ToList(),
            SortMode.ShortAsc => items.OrderBy(x => x.CharCount).ToList(),
            SortMode.ByApp => items.OrderBy(x => x.Kind).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByGroup => items.OrderBy(x => x.GroupId).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByType => items.OrderBy(x => x.Kind).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByPaste => items.OrderByDescending(x => x.LastPasteDate).ToList(),
            SortMode.ByRecentUse => items.OrderByDescending(x => x.LastPasteDate).ToList(),
            SortMode.ByAge => items.OrderBy(x => x.LastPasteDate).ToList(),
            _ => SortDescending ? items : Enumerable.Reverse(items).ToList()
        };
        _allFiltered = ordered;
        _loadedPage = 1;

        var firstPage = ordered.Take(PageSize).ToList();
        Items.Clear();
        foreach (var i in firstPage) Items.Add(new ClipRowVM(i, _privateIds.Contains(i.Id)));

        HasMore = ordered.Count > firstPage.Count;
        LoadMoreText = HasMore
            ? $"已加载 {Items.Count} / {ordered.Count} — 点击加载更多"
            : ordered.Count == 0 ? "" : $"已全部加载（{Items.Count} 条）";

        Selected = Items.FirstOrDefault();
        StatusText = $"{Items.Count} 条 · {Title}";
        _ = WarmThumbnailsAsync();
    }

    // ───────── 分页 ─────────

    private const int PageSize = 500;
    private int _loadedPage = 1;
    private List<ClipItem> _allFiltered = new();

    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _loadMoreText = "";

    [RelayCommand]
    private void LoadMore()
    {
        if (!HasMore) return;

        _loadedPage++;
        var take = _loadedPage * PageSize;
        var slice = _allFiltered.Take(take).ToList();

        Items.Clear();
        foreach (var i in slice) Items.Add(new ClipRowVM(i, _privateIds.Contains(i.Id)));

        HasMore = _allFiltered.Count > slice.Count;
        LoadMoreText = HasMore
            ? $"已加载 {Items.Count} / {_allFiltered.Count} — 点击加载更多"
            : $"已全部加载（{Items.Count} 条）";

        StatusText = $"{Items.Count} 条 · {Title}";
    }

    // ───────── 斜杠命令 ─────────

    public ObservableCollection<SlashItemVM> SlashItems { get; } = new();

    /// <summary>供 View 调用，确保斜杠列表已构建。</summary>
    public void BuildSlashItemsPublic()
    {
        if (SlashItems.Count == 0) BuildSlashItems();
    }

    private void BuildSlashItems()
    {
        SlashItems.Clear();
        foreach (var item in SlashCatalog.All) SlashItems.Add(item);
    }

    /// <summary>执行一条斜杠命令（直接应用筛选，不往输入框写前缀）。</summary>
    public void RunSlashCommand(SlashItemVM item)
    {
        IsSlashOpen = false;

        _suppressSearch = true;
        try { SearchText = ""; }
        finally { _suppressSearch = false; }

        switch (item.Alias)
        {
            case "tag":
                IsFilterPanelOpen = true;
                StatusText = "在筛选面板里选标签";
                break;
            case "type":
                IsFilterPanelOpen = true;
                StatusText = "在筛选面板里选类型";
                break;
            case "from":
                IsFilterPanelOpen = true;
                StatusText = "在筛选面板里选来源";
                break;
            case "age":
                IsFilterPanelOpen = true;
                StatusText = "在筛选面板里选时间范围";
                break;
            case "secret":
                SetFilterKind(ClipKind.Secret, "凭据");
                StatusText = "已筛选：只看凭据";
                break;
            case "ai":
                // 打开 AI 抽屉；如果 /ai 后面有内容，自动作为第一条消息
                var aiText = (SearchText ?? "").Trim();
                if (aiText.StartsWith("/ai", StringComparison.OrdinalIgnoreCase))
                    aiText = aiText[3..].Trim();
                if (aiText.StartsWith("ai", StringComparison.OrdinalIgnoreCase) && aiText.Length <= 3)
                    aiText = "";

                _suppressSearch = true;
                try { SearchText = ""; }
                finally { _suppressSearch = false; }

                RequestOpenAiDrawer?.Invoke(aiText);
                break;
            case "clear":
                ClearAllFiltersCommand.Execute(null);
                break;
        }
    }

    [ObservableProperty] private bool _isSlashOpen;

    // ───────── AI 语义搜索 ─────────

    [ObservableProperty] private bool _isAiSearching;
    [ObservableProperty] private string _aiSearchHint = "";
    [ObservableProperty] private bool _hasAiSuggestion;

    /// <summary>AI 状态条是否可见（搜索中 / 有结果 / 有错误）。</summary>
    public bool AiBarVisible => IsAiSearching || HasAiSuggestion || AiSearchHint.Length > 0;

    /// <summary>当前结果是 AI 排过序的。</summary>
    [ObservableProperty] private bool _isAiRanked;

    /// <summary>手动触发 AI 语义搜索（对当前结果重排）。</summary>
    [RelayCommand]
    private async Task RunAiSearch()
    {
        if (IsAiSearching) return;

        var query = (SearchText ?? "").Trim();
        if (query.Length == 0) { StatusText = "先输入要搜索的内容"; return; }

        if (_ai is null) { StatusText = "AI 服务未初始化"; return; }
        if (!_ai.Enabled) { StatusText = "AI 没启用（设置 → AI 填 Key 并打开开关）"; return; }

        var candidates = _allFiltered.Count > 0
            ? _allFiltered.Take(60).ToList()
            : Items.Select(x => x.Source).Take(60).ToList();

        if (candidates.Count == 0)
        {
            StatusText = "没有候选条目可分析";
            return;
        }

        IsAiSearching = true;
        HasAiSuggestion = false;
        AiSearchHint = $"AI 正在分析 {candidates.Count} 条候选…";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await _ai.RankAsync(
                query,
                candidates.Select(c => (c.Id, c.Preview)).ToList());

            sw.Stop();

            if (!result.Success)
            {
                AiSearchHint = "AI 失败：" + (result.Message ?? "未知错误");
                StatusText = AiSearchHint;
                return;
            }

            var ids = (result.Message ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => long.TryParse(x.Trim(), out var n) ? n : 0)
                .Where(n => n > 0)
                .ToList();

            if (ids.Count == 0)
            {
                AiSearchHint = $"AI 认为这 {candidates.Count} 条都不相关";
                StatusText = AiSearchHint;
                return;
            }

            // 按 AI 顺序重排
            var map = CandidatesToItems().ToDictionary(x => x.Id, x => x);
            var ordered = ids.Where(map.ContainsKey).Select(id => map[id]).ToList();

            Items.Clear();
            foreach (var it in ordered) Items.Add(new ClipRowVM(it));

            IsAiRanked = true;
            HasAiSuggestion = false;
            AiSearchHint = $"AI 找到 {ordered.Count} 条（{sw.Elapsed.TotalSeconds:F1} 秒）";
            StatusText = AiSearchHint;
            Selected = Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AiSearchHint = "AI 搜索异常：" + ex.Message;
            StatusText = AiSearchHint;
        }
        finally
        {
            IsAiSearching = false;
        }
    }

    /// <summary>把当前缓存的候选还原成 ClipItem 列表。</summary>
    private IEnumerable<ClipItem> CandidatesToItems()
    {
        if (_allFiltered.Count > 0) return _allFiltered;
        return Items.Select(x => x.Source);
    }

    /// <summary>搜索结果太少时，给出「用 AI 试试」的提示。</summary>
    partial void OnAiSearchHintChanged(string value)
        => OnPropertyChanged(nameof(AiBarVisible));

    partial void OnIsAiSearchingChanged(bool value)
        => OnPropertyChanged(nameof(AiBarVisible));

    private void MaybeSuggestAi(string query)
    {
        var threshold = App.Config.AiSearchThreshold;
        HasAiSuggestion = false;

        if (threshold <= 0) return;
        if (string.IsNullOrWhiteSpace(query)) return;
        if (_ai is null || !_ai.Enabled) return;
        if (Items.Count >= threshold) return;

        HasAiSuggestion = true;
        AiSearchHint = $"只找到 {Items.Count} 条，试试 AI 语义搜索";
    }

    /// <summary>把视图 ID 翻译成查询条件。</summary>
    private static (long? GroupId, ClipKind? Kind) ResolveView(long viewId) => viewId switch
    {
        -1 => (null, null),
        -2 => (null, null),
        -3 => (null, ClipKind.Image),
        -4 => (null, ClipKind.Link),
        -5 => (null, ClipKind.Code),
        -6 => (-1, null),
        -7 => (null, ClipKind.Secret),
        -12 => (null, null),   // 隐私：用标签过滤，不走 kind
        -8 => (null, ClipKind.File),
        -9 => (null, ClipKind.Command),
        -10 => (null, ClipKind.LongText),
        -11 => (null, ClipKind.Contact),
        _ => (viewId, null)
    };

    /// <summary>侧栏所有可用视图的定义。</summary>
    private static readonly (long Id, string Key, string Name, string Icon, string CountKey)[] AllViews =
    {
        (-1, "all", "全部历史", "Layers", "total"),
        (-7, "secrets", "凭据", "Shield", "secrets"),
        (-12, "privacy", "隐私", "Lock", "privacy"),
        (-3, "images", "图片", "Image", "images"),
        (-4, "links", "链接", "Link", "links"),
        (-5, "code", "代码", "Code", "code"),
        (-8, "files", "文件", "FileText", "files"),
        (-9, "commands", "命令", "Terminal", "commands"),
        (-10, "longtext", "长文本", "AlignLeft", "longtext"),
        (-11, "contacts", "邮箱/电话", "Mail", "contacts"),
        (-2, "today", "今天", "Clock", "today"),
        (-6, "uncategorized", "未归类", "Filter", "uncategorized"),
    };

    /// <summary>按 config 里的顺序与显隐生成侧栏。</summary>
    private void BuildViews(IReadOnlyDictionary<string, int> counts)
    {
        var cfg = App.Config.SidebarItems ?? new List<SidebarItemConfig>();
        var ordered = cfg.Count == 0
            ? AllViews.Select(v => new SidebarItemConfig { Key = v.Key, Visible = true }).ToList()
            : cfg;

        Views.Clear();
        bool first = true;

        foreach (var item in ordered)
        {
            if (!item.Visible) continue;
            var def = AllViews.FirstOrDefault(v => v.Key == item.Key);
            if (def.Key is null) continue;

            var name = def.Key switch
            {
                "all" => L.T("main.view.all"),
                "today" => L.T("main.view.today"),
                "images" => L.T("main.view.images"),
                "links" => L.T("main.view.links"),
                "code" => L.T("main.view.code"),
                "uncategorized" => L.T("main.view.uncategorized"),
                _ => def.Name
            };

            var count = def.CountKey.Length > 0 ? counts.GetValueOrDefault(def.CountKey) : 0;
            Views.Add(new GroupRowVM(def.Id, name, count, def.Icon) { IsSelected = first });
            first = false;
        }

        foreach (var def in AllViews)
        {
            if (ordered.Any(o => o.Key == def.Key)) continue;
            var count = def.CountKey.Length > 0 ? counts.GetValueOrDefault(def.CountKey) : 0;
            Views.Add(new GroupRowVM(def.Id, def.Name, count, def.Icon));
        }
    }


    /// <summary>按展开状态重建分组列表（递归，支持任意深度）。</summary>
    private void RebuildGroups()
    {
        Groups.Clear();
        var key = (GroupFilter ?? "").Trim();

        // 递归展开
        void Walk(IReadOnlyList<GroupNode> rawNodes, int depth, string parentPrefix,
                  bool parentIsLast, bool ancestorMatched, long parentId)
        {
            var nodes = OrderNodes(rawNodes).ToList();
            for (int i = 0; i < nodes.Count; i++)
            {
                var g = nodes[i];
                var isLast = i == nodes.Count - 1;

                // 搜索过滤：自己匹配 或 后代匹配 或 祖先已匹配
                bool selfMatch = key.Length == 0
                    || g.Name.Contains(key, StringComparison.OrdinalIgnoreCase);
                bool descMatch = key.Length == 0 || HasMatchingDescendant(g, key);

                if (!ancestorMatched && !selfMatch && !descMatch) continue;

                // 连接线：从 depth 1 开始
                // 用半角符号，避免某些中文字体把 └ 渲染成 L
                var connector = depth == 0
                    ? ""
                    : parentPrefix + (isLast ? "└─ " : "├─ ");

                var row = new GroupRowVM(g.Id, g.Name, g.Count,
                    depth == 0 ? "Folder" : "Circle",
                    hasChildren: g.Children.Count > 0)
                {
                    Depth = depth,
                    Connector = connector,
                    IsLastChild = isLast,
                    ParentId = parentId          // ← 真的父 ID
                };

                var expanded = _expandedStates.TryGetValue(g.Id, out var e) ? e : true;
                row.IsExpanded = expanded;

                Groups.Add(row);

                if (expanded && g.Children.Count > 0)
                {
                    // 给下一层准备前缀：父级是末位就画空格，否则画竖线
                    var nextPrefix = depth == 0
                        ? ""
                        : parentPrefix + (isLast ? "  " : "│ ");
                    Walk(g.Children, depth + 1, nextPrefix, isLast,
                         ancestorMatched || selfMatch, g.Id);
                }
            }
        }

        Walk(_groupTree, 0, "", true, false, 0);
    }

    /// <summary>是否有匹配搜索词的后代。</summary>
    private static bool HasMatchingDescendant(GroupNode node, string key)
    {
        foreach (var c in node.Children)
        {
            if (c.Name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
            if (HasMatchingDescendant(c, key)) return true;
        }
        return false;
    }

    private readonly Dictionary<long, bool> _expandedStates = new();

    [RelayCommand]
    private void ToggleExpand(GroupRowVM? row)
    {
        if (row is null || !row.HasChildren) return;
        row.IsExpanded = !row.IsExpanded;
        _expandedStates[row.Id] = row.IsExpanded;
        RebuildGroups();
    }

    [RelayCommand]
    private void SelectView(GroupRowVM? view)
    {
        if (view is null) return;
        SelectedView = view;
    }

    [RelayCommand]
    private async Task ToggleSortAsync()
    {
        SortDescending = !SortDescending;
        await ReloadForViewAsync();
    }

    /// <summary>点「AI」按钮：把搜索框切到 ai: 模式并聚焦。</summary>
    [RelayCommand]
    private void StartAiSearch()
    {
        SearchText = "ai:";
        IsCommandPanelOpen = false;
    }

    // ───────── 斜杠命令 ─────────

    public void BuildCommands()
    {
        _allCommands.Clear();
        _allCommands.Add(new SlashCommandVM("/all", "全部历史", "显示所有条目", () => SelectViewById(-1)));
        _allCommands.Add(new SlashCommandVM("/today", "今天", "只看今天复制的内容", () => SelectViewById(-2)));
        _allCommands.Add(new SlashCommandVM("/image", "图片", "只看图片条目", () => SelectViewById(-3)));
        _allCommands.Add(new SlashCommandVM("/link", "链接", "只看链接", () => SelectViewById(-4)));
        _allCommands.Add(new SlashCommandVM("/code", "代码", "只看代码片段", () => SelectViewById(-5)));
        _allCommands.Add(new SlashCommandVM("/ai", "AI 搜索", "用自然语言检索（需要配置 Key）", () => StartAiSearch()));
        _allCommands.Add(new SlashCommandVM("/settings", "打开设置", "跳到设置页", () => ShowSettingsPageCommand.Execute(null)));
        _allCommands.Add(new SlashCommandVM("/stats", "统计", "查看使用统计", () => ShowStatsPageCommand.Execute(null)));
        _allCommands.Add(new SlashCommandVM("/tag", "标签:", "按标签筛选，后面输入标签名", () => SetPrefix("tag:")));
        _allCommands.Add(new SlashCommandVM("/kind", "类型:", "按类型筛选（image/link/code/file）", () => SetPrefix("kind:")));
        _allCommands.Add(new SlashCommandVM("/from", "来源:", "按来源程序筛选", () => SetPrefix("from:")));
        _allCommands.Add(new SlashCommandVM("/clear", "清空搜索", "回到全部历史", () =>
        {
            SearchText = "";
            SelectViewById(-1);
        }));

        FilterCommands("");
    }

    /// <summary>按输入的命令前缀过滤（输入 /to 只留 /today）。</summary>
    private void FilterCommands(string input)
    {
        var key = (input ?? "").Trim();

        Commands.Clear();
        foreach (var c in _allCommands)
        {
            if (key.Length <= 1 || c.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                Commands.Add(c);
        }
    }

    /// <summary>选中一个前缀，搜索框保持空，等用户继续输入。</summary>
    private void SetPrefix(string prefix)
    {
        ActivePrefix = prefix;

        _suppressSearch = true;
        try { SearchText = ""; }
        finally { _suppressSearch = false; }

        IsCommandPanelOpen = false;
        StatusText = $"已选择 {prefix}，请继续输入";
    }

    private void SelectViewById(long id)
    {
        var v = Views.FirstOrDefault(x => x.Id == id) ?? Groups.FirstOrDefault(x => x.Id == id);
        if (v is not null) SelectedView = v;

        _suppressSearch = true;
        try { SearchText = ""; }
        finally { _suppressSearch = false; }

        IsCommandPanelOpen = false;
    }


    public bool IsClipPageVisible => CurrentPage == ShellPage.Clips;
    public bool IsSettingsVisible => CurrentPage == ShellPage.Settings;
    public bool IsStatsVisible => CurrentPage == ShellPage.Stats;

    partial void OnCurrentPageChanged(ShellPage value)
    {
        try { Services.AiSessionGuard.WindowState("Page=" + value); } catch { }

        OnPropertyChanged(nameof(IsClipPageVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsStatsVisible));
    }

    [RelayCommand] private void ShowSettingsPage() => CurrentPage = ShellPage.Settings;
    [RelayCommand] private void ShowStatsPage() => CurrentPage = ShellPage.Stats;
    [RelayCommand] private void ShowClipsPage() => CurrentPage = ShellPage.Clips;

    [ObservableProperty] private string _title = L.T("main.title.all");

    public Task<ClipDetail?> GetDetailAsync(long id) => _reader.GetDetailAsync(id);
    public Task<byte[]?> GetDibAsync(long id) => _reader.GetRawFormatAsync(id, "CF_DIB");

    public async Task LoadAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var items = await _reader.GetLatestAsync(200, null);
        Items.Clear();
        foreach (var i in items) Items.Add(new ClipRowVM(i));

        TotalCount = await _reader.GetTotalCountAsync();
        sw.Stop();
        LoadMs = sw.ElapsedMilliseconds;
        StatusText = $"{Items.Count} / {TotalCount:N0} · 载入 {LoadMs} ms";

        // 用 C# 分类计数（准确），首次约 150ms，之后走缓存
        var kindCounts = await _reader.GetKindCountsAsync();
        var sqlCounts = await _reader.GetViewCountsAsync();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["total"] = kindCounts.GetValueOrDefault("total"),
            ["today"] = sqlCounts.GetValueOrDefault("today"),
            ["images"] = kindCounts.GetValueOrDefault("image"),
            ["links"] = kindCounts.GetValueOrDefault("link"),
            ["secrets"] = kindCounts.GetValueOrDefault("secret"),
            ["files"] = kindCounts.GetValueOrDefault("file"),
            ["commands"] = kindCounts.GetValueOrDefault("command"),
            ["longtext"] = kindCounts.GetValueOrDefault("longtext"),
            ["contacts"] = kindCounts.GetValueOrDefault("contact"),
            ["uncategorized"] = sqlCounts.GetValueOrDefault("uncategorized"),
            ["code"] = kindCounts.GetValueOrDefault("code"),
            ["privacy"] = 0,
        };

        // 先查隐私（必须在 BuildViews 之前，否则侧栏数字是 0）
        if (_tagStore is not null)
        {
            try
            {
                var priv = await _tagStore.GetClipIdsByTagsAsync(
                    new List<string> { "__private" }, false);
                _privateIds = priv.Select(x => x.ClipId).ToHashSet();
                counts["privacy"] = _privateIds.Count;
                System.Diagnostics.Debug.WriteLine("[privacy] found " + _privateIds.Count + " ids");
            }
            catch (Exception ex)
            {
                _privateIds = new HashSet<long>();
                System.Diagnostics.Debug.WriteLine("[privacy] query failed: " + ex.Message);
            }
        }
        else
        {
            _privateIds = new HashSet<long>();
            System.Diagnostics.Debug.WriteLine("[privacy] tagStore is null");
        }

        Views.Clear();
        // 重建侧栏后保持原来的选中项（按 Id 匹配）
        var keepId = SelectedView?.Id;
        BuildViews(counts);

        if (keepId is not null)
        {
            var match = Views.FirstOrDefault(v => v.Id == keepId.Value);
            if (match is not null)
            {
                foreach (var v in Views) v.IsSelected = false;
                match.IsSelected = true;
                SelectedView = match;
            }
        }

        _groupTree = (await _reader.GetGroupTreeAsync()).ToList();
        RebuildGroups();

        var subCount = _groupTree.Sum(g => g.Children.Count);
        GroupSummary = $"分组 · {_groupTree.Count} 顶层 / {subCount} 子组";

        Selected = Items.FirstOrDefault();
        if (SelectedView is null) SelectedView = Views.FirstOrDefault();
        BuildCommands();
        _ = LoadTagsAsync();
        _ = WarmThumbnailsAsync();
    }

    /// <summary>
    /// 拖动调整分组顺序：把 srcId 排到 targetId 之前（或之后）。
    /// 排序偏好存本地（不动 Ditto 的 clipOrder / clipGroupOrder）。
    /// </summary>
    public void ReorderGroup(long srcId, long targetId, bool afterTarget = false)
    {
        var srcRow = Groups.FirstOrDefault(g => g.Id == srcId);
        var dstRow = Groups.FirstOrDefault(g => g.Id == targetId);
        if (srcRow is null || dstRow is null) return;

        // 同一父级才能重排
        if (srcRow.ParentId != dstRow.ParentId)
        {
            StatusText = "只能在同级分组之间调整顺序";
            return;
        }

        // 取同层所有分组的顺序；按当前 Groups 里的位置
        var siblings = Groups
            .Where(g => g.ParentId == srcRow.ParentId)
            .ToList();

        var srcIndex = siblings.FindIndex(g => g.Id == srcId);
        var dstIndex = siblings.FindIndex(g => g.Id == targetId);
        if (srcIndex < 0 || dstIndex < 0) return;

        // 从顺序里摘掉源，插到目标位置
        var ordered = siblings.Select(g => g.Id).ToList();
        ordered.RemoveAt(srcIndex);
        var insertAt = afterTarget ? dstIndex : dstIndex;
        if (srcIndex < dstIndex) insertAt = afterTarget ? dstIndex : dstIndex - 1;
        insertAt = Math.Max(0, Math.Min(insertAt, ordered.Count));
        ordered.Insert(insertAt, srcId);

        // 写本地顺序
        _localOrder.Clear();
        for (int i = 0; i < ordered.Count; i++)
            _localOrder[ordered[i]] = i;

        _groupSort = "local";
        RebuildGroups();

        StatusText = afterTarget
            ? $"「{srcRow.Name}」已排到「{dstRow.Name}」之后"
            : $"「{srcRow.Name}」已排到「{dstRow.Name}」之前";
    }

    /// <summary>本地拖动排序偏好（group_id → 排序权重）。</summary>
    private readonly Dictionary<long, double> _localOrder = new();

    private static Domain.Models.GroupNode? FindNode(
        IReadOnlyList<Domain.Models.GroupNode> nodes, long id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            var r = FindNode(n.Children, id);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>分组排序方式（本地偏好，不改 Ditto 数据）。</summary>
    private string _groupSort = "default";

    /// <summary>全部展开 / 折叠。</summary>
    public void ExpandAllGroups(bool expand)
    {
        foreach (var g in _groupTree) _expandedStates[g.Id] = expand;
        RebuildGroups();
    }

    /// <summary>分组排序：default / name / count_desc / count_asc。</summary>
    public void SortGroups(string mode)
    {
        _groupSort = mode;
        RebuildGroups();
        StatusText = mode switch
        {
            "name" => "已按名称排序",
            "count_desc" => "已按数量从多到少排序",
            "count_asc" => "已按数量从少到多排序",
            _ => "已恢复默认顺序"
        };
    }

    /// <summary>对一组 node 按当前排序方式排序。</summary>
    private IEnumerable<Domain.Models.GroupNode> OrderNodes(
        IEnumerable<Domain.Models.GroupNode> nodes)
        => _groupSort switch
        {
            "name" => nodes.OrderBy(n => n.Name, StringComparer.CurrentCulture),
            "count_desc" => nodes.OrderByDescending(n => n.Count).ThenBy(n => n.Name),
            "count_asc" => nodes.OrderBy(n => n.Count).ThenBy(n => n.Name),
            "local" => nodes.OrderBy(n => _localOrder.TryGetValue(n.Id, out var w) ? w : n.Id),
            _ => nodes
        };

    /// <summary>
    /// 只重新读分组树并重建（不动列表）。
    /// 删除/移动条目后调用，让左侧分组右侧的数字刷新。
    /// </summary>
    public async Task RefreshGroupCountsAsync()
    {
        try
        {
            _groupTree = (await _reader.GetGroupTreeAsync()).ToList();
            RebuildGroups();

            var subCount = _groupTree.Sum(g => g.Children.Count);
            GroupSummary = $"分组 · {_groupTree.Count} 顶层 / {subCount} 子组";
        }
        catch { }
    }

    private async Task WarmThumbnailsAsync()
    {
        foreach (var row in Items.Where(r => r.IsImage))
            await row.LoadThumbAsync(_reader, _thumbs);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        // ai: 前缀 -> 语义排序
        if (SearchText.StartsWith("ai:", StringComparison.OrdinalIgnoreCase))
        {
            await SemanticSearchAsync(SearchText[3..].Trim());
            return;
        }

        var fullQuery = ActivePrefix + SearchText;

        // tag: 前缀走标签查询
        if (ActivePrefix == "tag:" && _tagStore is not null && SearchText.Trim().Length > 0)
        {
            var hits = await _tagStore.GetClipIdsByTagsAsync(
                SearchText.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                StrictTagMode);

            var idSet = hits.Select(h => h.ClipId).ToHashSet();
            var latest = await _reader.GetLatestAsync(800, null);
            Items.Clear();
            foreach (var i in latest.Where(x => idSet.Contains(x.Id))) Items.Add(new ClipRowVM(i));
            Selected = Items.FirstOrDefault();
            StatusText = $"{Items.Count} 条 · tag:{SearchText}";
            return;
        }

        var (groupId, kind) = ResolveView(_activeViewId);
        var items = await _reader.SearchAsync(fullQuery, groupId, kind);

        // 时间范围筛选
        if (_filterAge is not null)
        {
            var now = DateTimeOffset.Now;
            items = _filterAge switch
            {
                "today" => items.Where(x => x.CreatedAt.ToLocalTime().Date == DateTime.Today).ToList(),
                "7d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 7).ToList(),
                "30d" => items.Where(x => (now - x.CreatedAt).TotalDays <= 30).ToList(),
                _ => items
            };
        }

        var ordered = SortMode switch
        {
            SortMode.TimeAsc => items.OrderBy(x => x.CreatedAt).ToList(),
            SortMode.LongDesc => items.OrderByDescending(x => x.CharCount).ToList(),
            SortMode.ShortAsc => items.OrderBy(x => x.CharCount).ToList(),
            SortMode.ByApp => items.OrderBy(x => x.Kind).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByGroup => items.OrderBy(x => x.GroupId).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByType => items.OrderBy(x => x.Kind).ThenByDescending(x => x.CreatedAt).ToList(),
            SortMode.ByPaste => items.OrderByDescending(x => x.LastPasteDate).ToList(),
            SortMode.ByRecentUse => items.OrderByDescending(x => x.LastPasteDate).ToList(),
            SortMode.ByAge => items.OrderBy(x => x.LastPasteDate).ToList(),
            _ => SortDescending ? items : Enumerable.Reverse(items).ToList()
        };
        Items.Clear();
        foreach (var i in ordered) Items.Add(new ClipRowVM(i, _privateIds.Contains(i.Id)));
        Selected = Items.FirstOrDefault();
        StatusText = $"{Items.Count} 条匹配 · 总 {TotalCount:N0}";
        MaybeSuggestAi(SearchText ?? "");

        // 有结果就记一条历史（相同内容会去重挪到最前）
        if (Items.Count > 0 && (SearchText ?? "").Trim().Length >= 2)
            PushRecentSearch();
        _ = WarmThumbnailsAsync();
    }

    /// <summary>搜索框内容变化：/ 开头开命令面板，否则 250ms 防抖后搜索。</summary>
    partial void OnSearchTextChanged(string value)
    {
        if (value.StartsWith('/'))
        {
            BuildSlashItemsPublic();
            IsSlashOpen = true;
            return;
        }

        if (_suppressSearch) return;

        IsSlashOpen = false;
        IsSuggestOpen = false;
        _searchDebounce?.Stop();
        _searchDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce?.Stop();
            _ = SearchAsync();
        };
        _searchDebounce.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    /// <summary>正在执行命令时，抑制搜索框触发的防抖查询。</summary>
    

    /// <summary>执行命令面板里的一条命令。</summary>
    public void RunCommand(SlashCommandVM? cmd)
    {
        if (cmd is null) return;

        // 非前缀类命令：先清掉之前选中的前缀
        var isPrefixCmd = cmd.Name is "/tag" or "/kind" or "/from";

        _suppressSearch = true;
        try
        {
            IsCommandPanelOpen = false;
            _searchDebounce?.Stop();
            SearchText = "";
            if (!isPrefixCmd) ActivePrefix = "";
        }
        finally
        {
            _suppressSearch = false;
        }

        cmd.Run();
    }

    /// <summary>语义搜索：AI 从最近的条目里挑出相关的。</summary>
    private async Task SemanticSearchAsync(string query)
    {
        if (_ai is null) { StatusText = "AI 服务未初始化"; return; }
        if (string.IsNullOrWhiteSpace(query)) { StatusText = "请输入 ai: 后面的查询内容"; return; }

        var all = await _reader.GetLatestAsync(120, null);
        var candidates = all.Select(x => (x.Id, x.Preview)).ToList();

        StatusText = $"AI 正在从 {candidates.Count} 条里筛选…";
        var r = await _ai.RankAsync(query, candidates);

        if (!r.Success) { StatusText = r.Message; return; }

        if (string.IsNullOrWhiteSpace(r.Message))
        {
            Items.Clear();
            StatusText = "AI 没有找到相关内容";
            return;
        }

        var ids = r.Message.Split(',')
            .Select(x => long.TryParse(x.Trim(), out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

        var map = all.ToDictionary(x => x.Id);

        Items.Clear();
        foreach (var id in ids)
            if (map.TryGetValue(id, out var item)) Items.Add(new ClipRowVM(item));

        Selected = Items.FirstOrDefault();
        StatusText = $"AI 找到 {Items.Count} 条相关结果";
    }

    [RelayCommand]
    private async Task OpenPreviewAsync(ClipRowVM? row)
    {
        if (row is null) return;
        if (IsPreviewOpen && Selected?.Id == row.Id) { IsPreviewOpen = false; return; }

        Selected = row;
        var detail = await _reader.GetDetailAsync(row.Id);
        if (detail is null) return;

        PreviewTitle = row.Preview;
        PreviewKindLabel = row.KindLabel;
        PreviewKindBrush = row.KindBrush;
        PreviewTime = row.TimeText;
        PreviewId = row.Id;
        PreviewIsImage = row.IsImage;
        PreviewImage = null;

        if (row.IsImage)
        {
            PreviewText = "";
            var dib = await _reader.GetRawFormatAsync(row.Id, "CF_DIB");
            if (dib is not null)
                PreviewImage = await Task.Run(() => DibDecoder.Decode(dib, 1200));
        }
        else
        {
            _rawPreviewText = string.IsNullOrWhiteSpace(detail.FullText) ? "(无文本内容)" : detail.FullText;

            // 尝试取 HTML 富文本
            PreviewHtml = "";
            PreviewHasRichText = false;
            PreviewIsRich = false;
            try
            {
                var htmlBytes = await _reader.GetRawFormatAsync(row.Id, "HTML Format");
                var html = CfHtml.Extract(htmlBytes);
                if (!string.IsNullOrWhiteSpace(html) && html.Length < 200_000)
                {
                    PreviewHtml = html;
                    PreviewHasRichText = true;
                }
            }
            catch { }
            PreviewHasSecret = ClipRowVM.Detector.Value.HasSensitive(_rawPreviewText);
            PreviewShowRaw = false;
            ReloadPreviewText();
        }
        PreviewFormats = detail.Formats.Count == 0 ? "—" : string.Join(" · ", detail.Formats);
        PreviewSize = detail.SizeBytes >= 1_048_576
            ? $"{detail.SizeBytes / 1_048_576.0:F1} MB"
            : $"{detail.SizeBytes:N0} B";
        PreviewGroup = detail.Item.GroupId > 0 ? $"#{detail.Item.GroupId}" : L.T("main.view.uncategorized");
        PreviewPasteCount = "—";
        PreviewSource = "";
        if (_source is not null)
        {
            try { PreviewSource = await _source.DescribeSourceAsync(row.Id) ?? ""; }
            catch { }
        }

        PreviewSource = "";
        if (_source is not null)
        {
            try { PreviewSource = await _source.DescribeSourceAsync(row.Id) ?? ""; }
            catch { }
        }

        PreviewTags.Clear();
        if (_tags is not null)
        {
            try
            {
                var tags = await _tags.GetTagsAsync(row.Id);
                foreach (var t in tags) PreviewTags.Add(t);
            }
            catch { }
        }

        IsPreviewOpen = true;
    }

    [RelayCommand]
    private async Task AddTag(string? tag)
    {
        if (_tags is null || Selected is null) return;
        var r = await _tags.AddAsync(Selected.Id, tag);
        if (r.Success)
        {
            var list = await _tags.GetTagsAsync(Selected.Id);
            PreviewTags.Clear();
            foreach (var t in list) PreviewTags.Add(t);
        }
        StatusText = r.Message;
    }

    [RelayCommand]
    private async Task RemoveTag(string? tag)
    {
        if (_tags is null || Selected is null || tag is null) return;
        var r = await _tags.RemoveAsync(Selected.Id, tag);
        if (r.Success) PreviewTags.Remove(tag);
        StatusText = r.Message;
    }

    [RelayCommand] private Task PasteSelected() => DoPasteAsync(false);
    [RelayCommand] private Task PastePlainSelected() => DoPasteAsync(true);

    private async Task DoPasteAsync(bool plainText)
    {
        var row = Selected;
        if (row is null || _bridge is null) return;
        try
        {
            await _bridge.PasteAsync(row.Id, plainText);
            StatusText = plainText ? $"已纯文本粘贴 #{row.Id}" : $"已粘贴 #{row.Id}";
        }
        catch (Exception ex)
        {
            StatusText = $"粘贴失败：{ex.Message}";
        }
    }

    /// <summary>打开原图查看器（原尺寸解码，仅在点图片时触发）。</summary>
    [RelayCommand]
    private async Task OpenImageViewer()
    {
        if (!PreviewIsImage || PreviewId == 0) return;

        var dib = await _reader.GetRawFormatAsync(PreviewId, "CF_DIB");
        if (dib is null) return;

        var bmp = await Task.Run(() => DibDecoder.Decode(dib, 0));
        if (bmp is null) return;

        var win = new Views.ImageViewerWindow(bmp, PreviewTitle);
        win.Owner = System.Windows.Application.Current.MainWindow;
        win.ShowDialog();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeManager.Toggle();
        IsDark = ThemeManager.Current == "dark";
    }
}
