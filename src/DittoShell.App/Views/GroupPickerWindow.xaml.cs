using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

/// <summary>
/// 选一个目标分组（用于右键「移入分组」）。
/// A+C 方案：树形缩进 + 搜索 + 透明圆角。
/// </summary>
public partial class GroupPickerWindow : Window
{
    /// <summary>平铺的分组列表（调用方传进来）。</summary>
    public List<GroupRowVM> GroupList { get; set; } = new();

    /// <summary>选中的分组 ID（点击「移动」后才有值）。</summary>
    public long? SelectedGroupId { get; private set; }

    /// <summary>显示用的树形列表（含缩进、连接线）。</summary>
    public ObservableCollection<GroupPickerRow> Rows { get; } = new();

    private readonly Dictionary<long, bool> _expanded = new();
    private string _filter = "";

    public GroupPickerWindow()
    {
        InitializeComponent();
        Services.WindowResizeHelper.Enable(this, border: 4);

        GroupListBox.ItemsSource = Rows;

        Loaded += (_, _) =>
        {
            BuildTree();
            SearchBox.Focus();
        };
    }

    // ═══════ 构建树 ═══════

    private void BuildTree()
    {
        Rows.Clear();

        if (GroupList.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        // 按 ParentId 分组
        var byParent = GroupList
            .GroupBy(g => g.ParentId)
            .ToDictionary(k => k.Key, v => v.OrderBy(x => x.Name).ToList());

        // 防止 ParentId 指向不存在的组（避免无限递归）
        var validIds = GroupList.Select(g => g.Id).ToHashSet();
        foreach (var key in byParent.Keys.ToList())
        {
            if (key != 0 && !validIds.Contains(key))
            {
                // 父级不存在 → 当顶层处理
                if (!byParent.ContainsKey(0)) byParent[0] = new List<GroupRowVM>();
                byParent[0].AddRange(byParent[key]);
                byParent.Remove(key);
            }
        }

        var filterLower = _filter.Trim().ToLowerInvariant();
        bool filtering = filterLower.Length > 0;

        int total = 0;

        void Walk(long parentId, int depth, string prefix, bool ancestorMatched)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;

            // 过滤：只保留匹配的（或后代匹配的）
            var visible = new List<GroupRowVM>();
            foreach (var c in children)
            {
                bool selfMatch = !filtering || c.Name.ToLowerInvariant().Contains(filterLower);
                bool descMatch = filtering && HasMatch(c.Id, filterLower);
                if (!filtering || selfMatch || descMatch || ancestorMatched)
                    visible.Add(c);
            }

            for (int i = 0; i < visible.Count; i++)
            {
                var g = visible[i];
                bool isLast = i == visible.Count - 1;
                bool selfMatch = !filtering || g.Name.ToLowerInvariant().Contains(filterLower);

                // 连接线：深度 0 不画
                var connector = depth == 0
                    ? ""
                    : prefix + (isLast ? "└─" : "├─");

                var row = new GroupPickerRow(g, depth)
                {
                    Connector = connector,
                    HasChildren = byParent.ContainsKey(g.Id) && byParent[g.Id].Count > 0,
                    IsLast = isLast,
                    IsMatch = filtering && selfMatch
                };

                // 搜索时自动展开
                bool expanded = filtering
                    || (_expanded.TryGetValue(g.Id, out var e) ? e : depth == 0);

                row.IsExpanded = expanded;
                Rows.Add(row);
                total++;

                if (expanded)
                {
                    var nextPrefix = depth == 0
                        ? ""
                        : prefix + (isLast ? "  " : "│ ");
                    Walk(g.Id, depth + 1, nextPrefix, ancestorMatched || selfMatch);
                }
            }
        }

        Walk(0, 0, "", false);

        // 兜底：没挂到任何父级的组
        if (total == 0 && GroupList.Count > 0)
        {
            foreach (var g in GroupList)
                Rows.Add(new GroupPickerRow(g, 0));
            total = Rows.Count;
        }

        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasMatch(long parentId, string filterLower)
    {
        foreach (var g in GroupList.Where(x => x.ParentId == parentId))
        {
            if (g.Name.ToLowerInvariant().Contains(filterLower)) return true;
            if (HasMatch(g.Id, filterLower)) return true;
        }
        return false;
    }

    // ═══════ 搜索 ═══════

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_filter)
            ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(_filter)
            ? Visibility.Collapsed : Visibility.Visible;

        BuildTree();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    // ═══════ 交互 ═══════

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void Row_Enter(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is GroupPickerRow r) r.IsHover = true;
    }

    private void Row_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is GroupPickerRow r) r.IsHover = false;
    }

    private void RowArrow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupPickerRow r })
        {
            r.IsExpanded = !r.IsExpanded;
            if (r.Source is not null)
                _expanded[r.Source.Id] = r.IsExpanded;
            BuildTree();
            e.Handled = true;
        }
    }

    private void Group_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupPickerRow r })
        {
            // 单击直接选中并确认（保持原有的快速体验）
            SelectedGroupId = r.Source?.Id;
            if (r.Source is not null) _expanded[r.Source.Id] = true;
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroupId is null)
        {
            HintText.Text = "请先点一个分组";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>浮窗里的一行（含树形视觉属性）。</summary>
public sealed partial class GroupPickerRow : System.ComponentModel.INotifyPropertyChanged
{
    public GroupRowVM? Source { get; }

    public GroupPickerRow(GroupRowVM? source, int depth)
    {
        Source = source;
        _depth = depth;
    }

    private int _depth;

    public string Name => Source?.Name ?? "";
    public int Count => Source?.Count ?? 0;
    public Brush? DotBrush => Source?.DotBrush;
    public bool HasChildren { get; set; }

    public string Connector { get; set; } = "";
    public bool IsLast { get; set; }
    public bool IsMatch { get; set; }
    public bool IsSelectedMatch => IsMatch;

    public bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(ArrowGlyph));
        }
    }

    private bool _isHover;
    public bool IsHover
    {
        get => _isHover;
        set { if (_isHover != value) { _isHover = value; Raise(nameof(IsHover)); } }
    }

    // ── 视觉属性 ──

    public double TreeNodeIndent => _depth * 16;

    public string TreeNodeConnector => Connector;

    public bool TreeNodeConnectorVisible => _depth > 0 && Connector.Length > 0;

    public double TreeNodeFontSize => _depth switch
    {
        0 => 13,
        1 => 12,
        _ => 11.5
    };

    public FontWeight TreeNodeWeight => _depth == 0
        ? FontWeights.SemiBold
        : FontWeights.Normal;

    public Brush TreeNodeBrush => _depth switch
    {
        0 => Res("B.Text"),
        1 => Res("B.Text2"),
        _ => Res("B.Text3")
    };

    public string ArrowGlyph => IsExpanded ? "ChevronDown" : "ChevronRight";

    public bool ShowArrow => HasChildren;

    private static Brush Res(string key)
    {
        try
        {
            if (System.Windows.Application.Current.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return Brushes.Gray;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
