using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DittoShell.App.Services;
using DittoShell.App.ViewModels;

using DittoShell.Domain.Models;

namespace DittoShell.App.Views;

public partial class MainWindowView : Window
{
    private readonly MainViewModel _vm;

    private const double CollapsedWidth = 780;
    private const double ExpandedWidth = 1240;
    private const double PreviewWidth = 520;

    public MainWindowView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        InitHover();

        // WindowChrome 下，标题栏里的按钮要显式声明可点击，否则被当成拖动区
        Loaded += (_, _) =>
        {
            EnableChromeHitTest();
            AiDrawer.CloseRequested += () => SetAiDrawer(false);

            // 诊断：窗口状态变化时记录（排查「离开窗口就停」）
            StateChanged += (_, _) =>
                Services.AiSessionGuard.WindowState(WindowState.ToString());
            Deactivated += (_, _) =>
                Services.AiSessionGuard.WindowState("Deactivated");
            Activated += (_, _) =>
                Services.AiSessionGuard.WindowState("Activated");

            // /ai 命令 → 打开抽屉，可带初始问题
            _vm.RequestOpenAiDrawer += initial =>
            {
                SetAiDrawer(true);
                if (!string.IsNullOrWhiteSpace(initial))
                {
                    _vm.AiChat.InputText = initial;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_vm.AiChat.SendCommand.CanExecute(null))
                            _vm.AiChat.SendCommand.Execute(null);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            };
        };

        // 系统阴影：必须在窗口句柄创建后调
        SourceInitialized += (_, _) => Services.DwmShadow.Enable(this, 1);

        // 边缘缩放交给 WindowChrome（ResizeBorderThickness），不再手写 WM_NCHITTEST

        SettingsPageView.BackRequested += (_, _) => _vm.ShowClipsPageCommand.Execute(null);
        StatsPageView.BackRequested += (_, _) => _vm.ShowClipsPageCommand.Execute(null);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PreviewIsRich))
                RenderRichPreview();

            if (e.PropertyName == nameof(MainViewModel.IsPreviewOpen))
                ApplyPreviewState(_vm.IsPreviewOpen);
        };
    }

    /// <summary>把标题栏内的所有可交互元素标记为"可点击"（WindowChrome 要求）。</summary>

    /// <summary>所有右键菜单打开时统一定位（转发给 App 的全局处理器）。</summary>
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        DittoShell.App.App.MenuOpened(sender, e);

        // 兼容原有的菜单项文案更新
        if (sender is ContextMenu cm) UpdateMenuText(cm);
    }

    private void UpdateMenuText(ContextMenu menu)
    {
        // 条目菜单：多选时改文案
        if (_vm?.SelectedClips is { Count: > 1 } sel)
        {
            var n = sel.Count;
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && mi.Tag is string tag)
                {
                    mi.Header = tag switch
                    {
                        "delete" => $"删除选中的 {n} 条",
                        "privacy" => $"把 {n} 条标记为隐私",
                        "movegroup" => $"把 {n} 条移到分组…",
                        _ => mi.Header
                    };
                }
            }
        }
    }
    private void EnableChromeHitTest()
    {
        try
        {
            Walk(this);
        }
        catch { }

        void Walk(DependencyObject node)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);

                if (child is System.Windows.Controls.Button or System.Windows.Controls.TextBox
                    or System.Windows.Controls.ComboBox or System.Windows.Controls.CheckBox)
                {
                    System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(
                        (IInputElement)child, true);
                }

                Walk(child);
            }
        }
    }

    /// <summary>供托盘 / 外部切到设置页。</summary>
    public void GoToSettings() => _vm.CurrentPage = ShellPage.Settings;

    /// <summary>切到设置页的任意分页。</summary>
    public void GoToSettingsTab(string tag)
    {
        _vm.CurrentPage = ShellPage.Settings;
        SettingsPageView.NavigateTo(tag);
    }

    /// <summary>切到「AI」分页。</summary>
    public void GoToAiSettings()
    {
        _vm.CurrentPage = ShellPage.Settings;
        SettingsPageView.NavigateTo("ai");
    }

    /// <summary>切到「Ditto 设置」分页。</summary>
    public void GoToDittoSettings()
    {
        _vm.CurrentPage = ShellPage.Settings;
        SettingsPageView.NavigateTo("ditto");
    }

    /// <summary>供托盘 / 外部切到统计页。</summary>
    public void GoToStats() => _vm.CurrentPage = ShellPage.Stats;

    /// <summary>展开预览：窗口变宽 + 右列占位；收起：列宽归零 + 窗口收窄。</summary>
    private void ApplyPreviewState(bool open)
    {
        // 用星号比例，窗口缩小时预览会自动跟着缩，而不是溢出被裁
        PreviewColumn.Width = open ? new GridLength(1.05, GridUnitType.Star) : new GridLength(0);
        PreviewColumn.MinWidth = open ? 260 : 0;

        if (open && Width < ExpandedWidth) Width = ExpandedWidth;
    }


    // ───────── 悬停预览 ─────────

    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer =
        new() { Interval = TimeSpan.FromMilliseconds(600) };
    private ClipRowVM? _hoverRow;

    private void InitHover()
    {
        _hoverTimer.Tick += OnHoverTick;
    }

    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipRowVM row })
        {
            _hoverRow = row;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverTimer.Stop();
        HoverPop.IsOpen = false;
        _hoverRow = null;
    }

    private async void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        var row = _hoverRow;
        if (row is null) return;

        try
        {
            var detail = await _vm.GetDetailAsync(row.Id);
            if (detail is null || _hoverRow != row) return;

            System.Windows.Media.Imaging.BitmapSource? img = null;
            if (row.IsImage)
            {
                var dib = await _vm.GetDibAsync(row.Id);
                if (dib is not null) img = await Task.Run(() => Media.DibDecoder.Decode(dib, 640));
            }
            if (_hoverRow != row) return;

            HoverCard.Show(detail, img, row.KindBrush, row.KindLabel, row.TimeText);
            PlaceHoverNearCursor();
            HoverPop.IsOpen = true;
        }
        catch
        {
            // 悬停预览失败不影响主流程
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point pt);

    /// <summary>
    /// 把悬浮卡片放在鼠标右下方：卡片左上角 = 鼠标位置 + 偏移。
    /// 靠近屏幕右边/底部时自动翻到左侧/上方，避免被截断。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosRaw(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void PlaceHoverNearCursor()
    {
        if (!GetCursorPosRaw(out var p)) return;

        var source = PresentationSource.FromVisual(this);
        double sx = 1, sy = 1;
        if (source?.CompositionTarget is not null)
        {
            sx = source.CompositionTarget.TransformFromDevice.M11;
            sy = source.CompositionTarget.TransformFromDevice.M22;
        }

        double mx = p.X * sx;
        double my = p.Y * sy;

        // 卡片尺寸（HoverPreview 固定宽 420，高度按内容估算）
        const double cardW = 420;
        double cardH = Math.Max(240, HoverCard.ActualHeight > 0 ? HoverCard.ActualHeight : 420);

        double x = mx + 18;
        double y = my + 18;

        double screenRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (x + cardW > screenRight - 8) x = mx - cardW - 18;
        if (y + cardH > screenBottom - 8) y = my - cardH - 18;

        if (x < SystemParameters.VirtualScreenLeft + 8) x = SystemParameters.VirtualScreenLeft + 8;
        if (y < SystemParameters.VirtualScreenTop + 8) y = SystemParameters.VirtualScreenTop + 8;

        HoverPop.HorizontalOffset = x;
        HoverPop.VerticalOffset = y;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>开发用：截图时展开预览面板。</summary>
    public void ExpandPreviewForShot()
    {
        _vm.OpenPreviewCommand.Execute(_vm.Items.FirstOrDefault());
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // ───────── 左侧视图 / 分组点击 ─────────

    private void ViewItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupRowVM vm })
        {
            _vm.SelectViewCommand.Execute(vm);
            e.Handled = true;
        }
    }

    private void GroupItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupRowVM vm })
        {
            _vm.SelectViewCommand.Execute(vm);
            e.Handled = true;
        }
    }

    // ───────── 斜杠命令面板 ─────────

    private void CmdOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        _vm.IsCommandPanelOpen = false;
        e.Handled = true;
    }

    private void CmdItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SlashCommandVM cmd })
        {
            _vm.RunCommand(cmd);
            e.Handled = true;
        }
    }

    /// <summary>侧栏里的 ListBox 会吃掉滚轮，这里转发给外层 ScrollViewer。</summary>
    private void ListBox_ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (SideScroll is null) return;
        e.Handled = true;
        SideScroll.ScrollToVerticalOffset(SideScroll.VerticalOffset - e.Delta / 3.0);
    }

    // ══════ 搜索区交互 ══════

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.SearchText))
        {
            _vm.LoadRecentSearches();
            _vm.IsRecentOpen = _vm.RecentSearches.Count > 0;
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 延迟关闭，否则点候选时先触发 LostFocus
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.IsSuggestOpen = false;
            _vm.IsRecentOpen = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenSlashOnInput()
    {
        if (DataContext is MainViewModel vm && (vm.SearchText ?? "").TrimStart().StartsWith("/") && vm.SearchText!.Trim().Length == 1)
        {
            vm.BuildSlashItemsPublic();
            vm.IsSlashOpen = true;
        }
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemQuestion || e.Key == Key.Divide)
        {
            Dispatcher.BeginInvoke(new Action(OpenSlashOnInput), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        // 候选列表里上下移动
        if (_vm.IsSuggestOpen && _vm.Suggestions.Count > 0)
        {
            var idx = _vm.Suggestions.IndexOf(_vm.Suggestions.FirstOrDefault(x => x.IsHighlighted)!);

            if (e.Key == Key.Down)
            {
                var next = Math.Min(idx + 1, _vm.Suggestions.Count - 1);
                foreach (var s2 in _vm.Suggestions) s2.IsHighlighted = false;
                _vm.Suggestions[next].IsHighlighted = true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                var prev = Math.Max(idx - 1, 0);
                foreach (var s2 in _vm.Suggestions) s2.IsHighlighted = false;
                _vm.Suggestions[prev].IsHighlighted = true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                var pick = _vm.Suggestions.FirstOrDefault(x => x.IsHighlighted);
                if (pick is not null)
                {
                    _vm.AcceptSuggestion(pick);
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Escape)
            {
                _vm.IsSuggestOpen = false;
                e.Handled = true;
                return;
            }
        }

        // 输入时更新候选
        if (!IsTextInputKey(e.Key)) return;

        var next2 = BuildPreviewText(e.Key);
        if (next2 is null) return;

        await _vm.UpdateSuggestionsAsync(next2);
    }

    private static bool IsTextInputKey(Key k)
        => (k >= Key.A && k <= Key.Z) || (k >= Key.D0 && k <= Key.D9)
           || k == Key.OemSemicolon || k == Key.OemMinus || k == Key.OemComma;

    private string? BuildPreviewText(Key k)
    {
        var cur = _vm.SearchText ?? "";
        if (k >= Key.A && k <= Key.Z) return cur + char.ToLowerInvariant((char)('A' + (k - Key.A)));
        if (k >= Key.D0 && k <= Key.D9) return cur + (char)('0' + (k - Key.D0));
        if (k == Key.OemSemicolon) return cur + ":";
        return null;
    }

    private void SlashItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SlashItemVM item } && DataContext is MainViewModel vm)
        {
            vm.RunSlashCommand(item);
            e.Handled = true;
        }
    }

    private void Suggest_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchSuggestVM s } && DataContext is MainViewModel vm)
        {
            vm.AcceptSuggestion(s);
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecentSearchVM r } && DataContext is MainViewModel vm)
        {
            vm.AcceptRecent(r);
            e.Handled = true;
        }
    }

    private void FilterKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || DataContext is not MainViewModel vm) return;

        var (kind, label) = key switch
        {
            "image" => (ClipKind.Image, "图片"),
            "link" => (ClipKind.Link, "链接"),
            "code" => (ClipKind.Code, "代码"),
            "file" => (ClipKind.File, "文件"),
            "secret" => (ClipKind.Secret, "凭据"),
            "command" => (ClipKind.Command, "命令"),
            "longtext" => (ClipKind.LongText, "长文本"),
            "contact" => (ClipKind.Contact, "联系"),
            _ => ((ClipKind?)null, "")
        };
        vm.SetFilterKind(kind, label);
    }

    private void FilterAge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || DataContext is not MainViewModel vm) return;

        var label = key switch
        {
            "today" => "今天",
            "7d" => "最近 7 天",
            "30d" => "最近 30 天",
            _ => ""
        };
        vm.SetFilterAge(key.Length == 0 ? null : key, label);
    }

    private void FilterTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel vm) return;

        var tag = FilterTagBox.Text.Trim();
        if (tag.Length == 0) return;

        vm.AddFilterTag(tag);
        FilterTagBox.Text = "";
        e.Handled = true;
    }

    private void FilterPanel_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsFilterPanelOpen = !_vm.IsFilterPanelOpen;
    }

    private void RemoveFilterTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            _vm.RemoveFilterTagCommand.Execute(tag);
    }

    // ══════ AI 抽屉 ══════

    private void OpenAiDrawer_Click(object sender, RoutedEventArgs e) => SetAiDrawer(true);

    private async void OpenSession_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionItemVM item })
        {
            await _vm.AiChat.OpenSessionAsync(item);
            e.Handled = true;
        }
    }

    private void CloseAiDrawer_Click(object sender, RoutedEventArgs e) => SetAiDrawer(false);


    public void SetAiDrawer(bool open, int? width = null)
    {
        // 只在窗口极窄时收预览（宽屏就都留着，用户可以拖分隔条）
        if (open && ActualWidth < 1100 && _vm.IsPreviewOpen)
        {
            _vm.IsPreviewOpen = false;
            _vm.StatusText = "窗口太窄，已收起预览（可拖分隔条调）";
        }

        _vm.AiChat.IsOpen = open;
        AiDivider.Width = open ? new GridLength(4) : new GridLength(0);

        // 宽度自适应：窗口窄时用 360，宽时用配置值
        var desired = width ?? (App.Config.AiDrawerWidth > 200 ? App.Config.AiDrawerWidth : 460);
        var maxAllowed = Math.Max(320, ActualWidth - 140 - 260 - 60);
        var w = (int)Math.Min(desired, maxAllowed);
        AiColumn.Width = open ? new GridLength(w) : new GridLength(0);

        // 记住状态
        App.Config.AiDrawerWidth = open ? w : 0;
        App.SaveConfig();

        if (open)
        {
            AiDrawer.Attach(_vm.AiChat);
            AiDrawer.FocusInput();
        }
    }

    /// <summary>窗口尺寸变化时重新平衡两栏。</summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只有极窄（< 1100）且两边都开时才收预览，避免误关
        if (e.NewSize.Width < 1100 && _vm.IsPreviewOpen && _vm.AiChat.IsOpen)
        {
            _vm.IsPreviewOpen = false;
            _vm.StatusText = "窗口太窄，已收起预览";
        }

        // 抽屉宽度跟着窗口收缩
        if (_vm.AiChat.IsOpen && AiColumn.Width.Value > 0)
        {
            var maxAllowed = Math.Max(320, e.NewSize.Width - 140 - 260 - 60);
            if (AiColumn.Width.Value > maxAllowed)
                AiColumn.Width = new GridLength(maxAllowed);
        }
    }

    // ══════ AI 抽屉拖动 ══════

    private double _aiDragStartWidth;

    private void AiSplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _aiDragStartWidth = AiColumn.Width.Value;
    }

    private void AiSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_vm.AiChat.IsOpen) return;

        // 只调 AI 列：往左拖变宽，往右拖变窄
        var current = AiColumn.Width.Value > 0 ? AiColumn.Width.Value : 460;
        var next = current - e.HorizontalChange;

        // 下限 320，上限留出侧栏 140 + 列表 260
        var maxAllowed = Math.Max(320, ActualWidth - 140 - 260 - 20);
        next = Math.Max(320, Math.Min(next, maxAllowed));

        AiColumn.Width = new GridLength(next);
    }

    private void AiSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // 记住用户拖的宽度
        if (AiColumn.Width.Value > 200)
        {
            App.Config.AiDrawerWidth = (int)AiColumn.Width.Value;
            App.SaveConfig();
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsPreviewOpen = false;
        _vm.StatusText = "预览已关闭";
    }

    /// <summary>
    /// 增量同步选中项。
    /// 关键：不用 SelectedItems 全量覆盖——滚动时容器回收会让它只含可见项，
    /// 覆盖后选中就丢了。只用 AddedItems / RemovedItems。
    /// </summary>
    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            foreach (var item in e.RemovedItems)
                if (item is ClipRowVM r) _vm.SelectedClips.Remove(r);

            foreach (var item in e.AddedItems)
                if (item is ClipRowVM r && !_vm.SelectedClips.Contains(r))
                    _vm.SelectedClips.Add(r);

            _vm.NotifySelectionCountChanged();

            // 单击（非多选）时更新当前项
            if (e.AddedItems.Count == 1 && _vm.SelectedClips.Count == 1
                && e.AddedItems[0] is ClipRowVM one)
                _vm.Selected = one;
        }
        catch { }
    }

    // ───────── 排序 / 条目右键 ─────────

    private void Sort_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // XAML 初始化时就会触发一次，此时 DataContext 还没赋值
        if (SortCombo is null || _vm is null) return;
        var mode = SortCombo.SelectedIndex switch
        {
            1 => SortMode.TimeAsc,
            2 => SortMode.LongDesc,
            3 => SortMode.ShortAsc,
            4 => SortMode.ByGroup,
            5 => SortMode.ByType,
            6 => SortMode.ByPaste,
            7 => SortMode.ByRecentUse,
            8 => SortMode.ByAge,
            _ => SortMode.TimeDesc
        };
        _vm.SetSortCommand.Execute(mode);
    }

    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var n = _vm.SelectedClips.Count;
        if (n <= 1) return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi) continue;

            mi.Header = mi.Tag switch
            {
                "delete" => $"删除选中的 {n} 条",
                "paste" => $"粘贴 {n} 条",
                "copy" => $"复制 {n} 条到剪贴板",
                "privacy" => $"把 {n} 条标记为隐私",
                "movegroup" => $"把 {n} 条移到分组…",
                "exporttxt" => $"导出这 {n} 条为文本",
                "exportjson" => $"导出这 {n} 条为 JSON",
                _ => mi.Header
            };
        }
    }

    private void ClipMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var row = _vm.Selected;
        if (row is null) return;

        switch (mi.Tag as string)
        {
            case "paste":
                _vm.PasteSelectedCommand.Execute(null);
                break;
            case "plain":
                _vm.PastePlainSelectedCommand.Execute(null);
                break;
            case "privacy":
                _ = SetPrivateBatchAsync(!row.IsPrivate);
                break;

            case "copy":
                try
                {
                    System.Windows.Clipboard.SetText(row.Preview);
                    _vm.StatusText = "已复制到剪贴板";
                }
                catch { _vm.StatusText = "复制失败"; }
                break;
            case "preview":
                _vm.OpenPreviewCommand.Execute(row);
                break;
            case "edit":
                _vm.OpenPreviewCommand.Execute(row);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PreviewTextBox?.Focus();
                    PreviewTextBox?.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Background);
                break;
            case "saveas":
                _vm.SaveAsNewCommand.Execute(null);
                break;
            case "movegroup":
                _ = MoveToGroupBatchAsync();
                break;
            case "opensource":
                _ = OpenSourceAppAsync(row);
                break;
            case "reveal":
                RevealInExplorer(row);
                break;
            case "delete":
                _ = DeleteClipAsync(row);
                break;

            // ── 二级菜单新增项 ──
            case "newgroup":
                _vm.StatusText = "请在左侧分组区右键新建";
                break;
            case "ungroup":
                _ = MoveToTopLevelAsync(row);
                break;
            case "copyhtml":
                _ = CopyFormatAsync(row, "HTML Format");
                break;
            case "copyimage":
                _ = CopyFormatAsync(row, "CF_DIB");
                break;
            case "filterby":
                _vm.SearchText = (row.Preview ?? "").Length > 40
                    ? row.Preview!.Substring(0, 40)
                    : (row.Preview ?? "");
                break;
            case "newitem":
                _vm.SaveAsNewCommand.Execute(null);
                break;
            case "props":
                ShowProperties(row);
                break;
            case "keep":
                _ = SetKeepAsync(row, true);
                break;
            case "unkeep":
                _ = SetKeepAsync(row, false);
                break;
            case "sticky":
                _ = SetStickyAsync(row, true);
                break;
            case "unsticky":
                _ = SetStickyAsync(row, false);
                break;
            case "exporttxt":
                _ = ExportAsync(row, "txt");
                break;
            case "exportjson":
                _ = ExportAsync(row, "json");
                break;
        }
    }

    private async Task MoveToTopLevelAsync(ClipRowVM row)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;
        var r = await svc.MoveClipsAsync(new[] { row.Id }, 0);
        _vm.StatusText = r.Message;
        if (r.Success) await _vm.LoadAsync();
    }

    private async Task CopyFormatAsync(ClipRowVM row, string format)
    {
        try
        {
            var reader = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.IClipReader))
                         as DittoShell.Domain.Abstractions.IClipReader;
            var detail = await reader!.GetDetailAsync(row.Id);

            if (detail?.Formats is null || !detail.Formats.Contains(format))
            {
                _vm.StatusText = $"这条没有 {format} 格式";
                return;
            }

            System.Windows.Clipboard.SetText(detail.FullText ?? "");
            _vm.StatusText = $"已复制（{format}）";
        }
        catch (Exception ex) { _vm.StatusText = "复制失败：" + ex.Message; }
    }

    private void ShowProperties(ClipRowVM row)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"条目 ID：{row.Id}");
        sb.AppendLine($"类型：{row.Kind}");
        sb.AppendLine($"时间：{row.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"长度：{(row.Preview?.Length ?? 0)} 字（预览）");
        sb.AppendLine($"隐私：{(row.IsPrivate ? "是" : "否")}");
        MessageBox.Show(this, sb.ToString(), "属性", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SetKeepAsync(ClipRowVM row, bool keep)
    {
        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) return;
        try
        {
            await writer.SetKeepAsync(row.Id, keep);
            _vm.StatusText = keep ? "已设为禁止自动删除" : "已允许自动删除";
        }
        catch (Exception ex) { _vm.StatusText = "操作失败：" + ex.Message; }
    }

    private async Task SetStickyAsync(ClipRowVM row, bool sticky)
    {
        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) return;
        try
        {
            await writer.SetStickyAsync(row.Id, sticky);
            _vm.StatusText = sticky ? "已置顶" : "已取消置顶";
            await _vm.LoadAsync();
        }
        catch (Exception ex) { _vm.StatusText = "操作失败：" + ex.Message; }
    }

    private async Task ExportAsync(ClipRowVM row, string format)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"clip-{row.Id}",
            Filter = format == "json" ? "JSON|*.json" : "文本|*.txt"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var reader = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.IClipReader))
                         as DittoShell.Domain.Abstractions.IClipReader;
            var detail = await reader!.GetDetailAsync(row.Id);
            var text = detail?.FullText ?? row.Preview ?? "";

            if (format == "json")
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = row.Id,
                    kind = row.Kind.ToString(),
                    created = row.CreatedAt,
                    text
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(dlg.FileName, json);
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(dlg.FileName, text);
            }

            _vm.StatusText = "已导出到 " + System.IO.Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex) { _vm.StatusText = "导出失败：" + ex.Message; }
    }

    private async Task MoveToGroupAsync(ClipRowVM row)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;

        var picker = new GroupPickerWindow { Owner = this, GroupList = _vm.Groups.ToList() };
        if (picker.ShowDialog() != true || picker.SelectedGroupId is null) return;

        var r = await svc.MoveClipsAsync(new[] { row.Id }, picker.SelectedGroupId.Value);
        _vm.StatusText = r.Message;
        if (r.Success) await _vm.LoadAsync();
    }

    private async Task OpenSourceAppAsync(ClipRowVM row)
    {
        try
        {
            var src = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ISourceStore))
                      as DittoShell.Domain.Abstractions.ISourceStore;
            var info = src is null ? null : await src.GetSourceAsync(row.Id);
            var app = info?.App;

            if (string.IsNullOrWhiteSpace(app))
            {
                _vm.StatusText = "这条没有来源记录";
                return;
            }

            var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name) { UseShellExecute = true });
            _vm.StatusText = "已尝试打开 " + name;
        }
        catch (Exception ex) { _vm.StatusText = "打开失败：" + ex.Message; }
    }

    private void RevealInExplorer(ClipRowVM row)
    {
        var text = (row.Preview ?? "").Trim();
        var looksLikePath = text.Length > 2 &&
            (text[1] == ':' || text.StartsWith("\\\\") || text.StartsWith("/"));

        if (!looksLikePath) { _vm.StatusText = "这条不是文件路径"; return; }

        try
        {
            if (System.IO.File.Exists(text) || System.IO.Directory.Exists(text))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{text}\"");
            else
                _vm.StatusText = "路径不存在：" + text;
        }
        catch (Exception ex) { _vm.StatusText = "打开失败：" + ex.Message; }
    }

    /// <summary>
    /// 直接从 ListBox 读取当前选中项。
    /// 不依赖 VM 的 SelectedClips 缓存（那个可能因为 SelectionChanged 时序问题而滞后）。
    /// </summary>
    private List<long> GetSelectedIdsFromList()
    {
        var ids = new List<long>();
        try
        {
            if (ItemsList?.SelectedItems is null) return ids;

            foreach (var item in ItemsList.SelectedItems)
                if (item is ClipRowVM row) ids.Add(row.Id);
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// 取要操作的 ID 列表：
    /// 优先用 ListBox 实际选中项，其次 VM 缓存，最后单条。
    /// </summary>
    private List<long> GetTargetIds(ClipRowVM fallback)
    {
        // 用 VM 的选中快照（由 SelectionChanged 增量维护，不受虚拟化影响）
        var snap = _vm.SelectedIdSnapshot();
        if (snap.Count > 0) return snap;

        // 兜底：ListBox 实际选中
        var fromList = GetSelectedIdsFromList();
        if (fromList.Count > 0) return fromList;

        return new List<long> { fallback.Id };
    }

    /// <summary>让类型计数 / 类型索引缓存失效，下次刷新重新统计。</summary>
    private static void InvalidateCountsCache()
    {
        try
        {
            var reader = App.ServiceProvider?.GetService(
                typeof(DittoShell.Infrastructure.DittoDb.DittoDbReader))
                as DittoShell.Infrastructure.DittoDb.DittoDbReader;

            reader?.InvalidateKindCounts();
        }
        catch { }
    }

    /// <summary>批量标记 / 取消隐私。</summary>
    private async Task SetPrivateBatchAsync(bool makePrivate)
    {
        var ids = _vm.SelectedIdSnapshot();
        if (ids.Count == 0 && _vm.Selected is not null)
            ids = new List<long> { _vm.Selected.Id };
        if (ids.Count == 0) return;

        var tags = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                   as DittoShell.Domain.Abstractions.ITagStore;
        if (tags is null) { _vm.StatusText = "标签服务未就绪"; return; }

        int done = 0;
        foreach (var id in ids)
        {
            try
            {
                if (makePrivate) await tags.AddTagAsync(id, "__private");
                else await tags.RemoveTagAsync(id, "__private");
                done++;
            }
            catch { }
        }

        // 同步 UI 标记
        foreach (var r in _vm.Items.Where(x => ids.Contains(x.Id)))
            r.IsPrivate = makePrivate;

        _vm.StatusText = makePrivate
            ? $"已把 {done} 条标记为隐私"
            : $"已取消 {done} 条的隐私标记";

        await _vm.RefreshGroupCountsAsync();
    }

    /// <summary>批量移到分组。</summary>
    private async Task MoveToGroupBatchAsync()
    {
        var ids = _vm.SelectedIdSnapshot();
        if (ids.Count == 0 && _vm.Selected is not null)
            ids = new List<long> { _vm.Selected.Id };
        if (ids.Count == 0) return;

        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;

        var picker = new GroupPickerWindow { Owner = this, GroupList = _vm.Groups.ToList() };
        if (picker.ShowDialog() != true || picker.SelectedGroupId is null) return;

        var r = await svc.MoveClipsAsync(ids, picker.SelectedGroupId.Value);
        _vm.StatusText = r.Message;
        if (r.Success)
        {
            await _vm.RefreshGroupCountsAsync();
            await _vm.LoadAsync();
        }
    }

    private async Task DeleteClipAsync(ClipRowVM row)
    {
        // 直接从 ListBox 读，避免缓存滞后导致只删一条
        var ids = GetTargetIds(row);

        // 诊断：如果选中数和 ListBox 数量差很多，说明 SelectionChanged 有问题
        var listCount = ItemsList?.SelectedItems?.Count ?? 0;
        if (listCount != ids.Count)
            _vm.StatusText = $"⚠ 选中 {listCount} 条，实际取到 {ids.Count} 条";

        var n = ids.Count;
        var msg = n == 1
            ? "确定删除这一条吗？"
            : $"确定删除选中的 {n} 条吗？";

        var ok = MessageBox.Show(this,
            msg + Environment.NewLine + Environment.NewLine
            + "条目会从列表消失，原始数据由 Ditto 在空闲时清理。" + Environment.NewLine
            + "删除前已自动备份数据库。",
            "删除条目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (ok != MessageBoxResult.OK) return;

        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) { _vm.StatusText = "写入通道未就绪"; return; }

        try
        {
            // 一次性删除（只备份一次）
            var done = await writer.DeleteClipsAsync(ids);
            _vm.StatusText = $"已删除 {done} 条";

            // 让类型计数缓存失效，否则侧栏数字不变
            InvalidateCountsCache();

            // 只移除被删的行（不重建整个列表，避免卡顿）
            var removed = _vm.Items.Where(x => ids.Contains(x.Id)).ToList();
            foreach (var r in removed) _vm.Items.Remove(r);
            _vm.SelectedClips.Clear();
            if (_vm.Selected is not null && ids.Contains(_vm.Selected.Id))
                _vm.Selected = _vm.Items.FirstOrDefault();

            // 刷新左侧分组右侧的数字
            await _vm.RefreshGroupCountsAsync();
            return;

            if (_vm.IsPreviewOpen && _vm.Selected is not null && ids.Contains(_vm.Selected.Id))
                _vm.IsPreviewOpen = false;
            await _vm.LoadAsync();
        }
        catch (Exception ex) { _vm.StatusText = "删除失败：" + ex.Message; }
    }

    private bool IsPreviewOpenFor(ClipRowVM row) => _vm.IsPreviewOpen && _vm.Selected?.Id == row.Id;

    private async Task PersistPrivacyAsync(ClipRowVM row)
    {
        try
        {
            var tags = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.TagService))
                       as DittoShell.Application.Services.TagService;
            if (tags is null) return;

            if (row.IsPrivate) await tags.AddAsync(row.Id, "__private");
            else await tags.RemoveAsync(row.Id, "__private");
        }
        catch { }
    }

    // ───────── 左侧视图 / 分组选中 ─────────

    private void Views_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewsList.SelectedItem is GroupRowVM vm) _vm.SelectViewCommand.Execute(vm);
    }

    private void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsList.SelectedItem is GroupRowVM vm)
        {
            _vm.SelectedGroupRow = vm;
            _vm.SelectViewCommand.Execute(vm);
        }
    }

    // ───────── 分组折叠 / 右键菜单 ─────────

    /// <summary>浮层里点标签胶囊。</summary>
    private void TagChip_Click2(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagChipVM chip })
        {
            _vm.ToggleTagCommand.Execute(chip);
            e.Handled = true;
        }
    }

    /// <summary>搜索栏的「标签」按钮：开/关浮层。</summary>
    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsTagPanelOpen = !_vm.IsTagPanelOpen;
    }

    private void GroupArrow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupRowVM vm })
        {
            _vm.ToggleExpandCommand.Execute(vm);
            e.Handled = true;
        }
    }

    private GroupRowVM? GroupFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.Parent is not ContextMenu cm) return null;
        return (cm.PlacementTarget as FrameworkElement)?.Tag as GroupRowVM;
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (g is null) return;
        if (!g.HasChildren) { _vm.StatusText = "这个分组没有子组"; return; }
        _vm.ToggleExpandCommand.Execute(g);
    }

    private async void GroupRename_Click(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (g is null) return;
        if (g.Id <= 0) { _vm.StatusText = "虚拟视图不能改名"; return; }

        var name = TextInputDialog.Prompt(this, "重命名分组", g.Name);
        if (string.IsNullOrWhiteSpace(name) || name == g.Name) return;

        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;

        var r = await svc.RenameAsync(g.Id, name);
        _vm.StatusText = r.Message;
        if (r.Success) await _vm.LoadAsync();
    }

    /// <summary>清空分组里的所有条目（真删，有确认+备份）。</summary>
    /// <summary>分组区右键：空白处 → 新建顶层分组。</summary>
    private void GroupsArea_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 点在分组行上时，让行的 ContextMenu 处理
        // 关键：用视觉树向上找，不用 .Parent（DataTemplate 里有 ContentPresenter 中间层）
        if (IsOnGroupRow(e.OriginalSource as DependencyObject)) return;

        var menu = new ContextMenu();
        menu.Opened += (s0, e0) => DittoShell.App.App.MenuOpened(s0, e0);

        var newTop = new MenuItem { Header = "新建顶层分组…" };
        newTop.Click += (_, _) => _ = CreateGroupAsync(0);
        menu.Items.Add(newTop);

        menu.Items.Add(new Separator());

        // 全部展开 / 折叠
        var expandAll = new MenuItem { Header = "全部展开" };
        expandAll.Click += (_, _) =>
        {
            _vm.ExpandAllGroups(true);
            _vm.StatusText = "已全部展开";
        };
        menu.Items.Add(expandAll);

        var collapseAll = new MenuItem { Header = "全部折叠" };
        collapseAll.Click += (_, _) =>
        {
            _vm.ExpandAllGroups(false);
            _vm.StatusText = "已全部折叠";
        };
        menu.Items.Add(collapseAll);

        menu.Items.Add(new Separator());

        // 排序
        var sortName = new MenuItem { Header = "按名称排序" };
        sortName.Click += (_, _) => { _vm.SortGroups("name"); };
        menu.Items.Add(sortName);

        var sortCount = new MenuItem { Header = "按数量排序（多→少）" };
        sortCount.Click += (_, _) => { _vm.SortGroups("count_desc"); };
        menu.Items.Add(sortCount);

        var sortCountAsc = new MenuItem { Header = "按数量排序（少→多）" };
        sortCountAsc.Click += (_, _) => { _vm.SortGroups("count_asc"); };
        menu.Items.Add(sortCountAsc);

        var sortDefault = new MenuItem { Header = "恢复默认顺序" };
        sortDefault.Click += (_, _) => { _vm.SortGroups("default"); };
        menu.Items.Add(sortDefault);

        menu.Items.Add(new Separator());

        // 如果当前有选中的分组，可以建子分组
        var cur = _vm.SelectedGroupRow;
        if (cur is not null && cur.IsRealGroup)
        {
            var newSub = new MenuItem { Header = $"在「{cur.Name}」下新建子分组…" };
            newSub.Click += (_, _) => _ = CreateGroupAsync(cur.Id);
            menu.Items.Add(newSub);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>新建分组（parentId = 0 表示顶层）。</summary>
    private async Task CreateGroupAsync(long parentId)
    {
        var dlg = new InputDialog("新建分组", "") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var writer = App.ServiceProvider?.GetService(
            typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
            as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) { _vm.StatusText = "写入通道未就绪"; return; }

        try
        {
            var id = await writer.CreateGroupAsync(name, parentId);
            _vm.StatusText = id > 0
                ? $"已新建分组「{name}」"
                : "新建失败（可能同名）";

            await _vm.RefreshGroupCountsAsync();
        }
        catch (Exception ex) { _vm.StatusText = "新建失败：" + ex.Message; }
    }

    private async void GroupPurge_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null || !row.IsRealGroup) { _vm.StatusText = "这不是可操作的分组"; return; }

        var count = row.Count;
        if (count == 0) { _vm.StatusText = "这个组是空的"; return; }

        var ok = MessageBox.Show(this,
            $"确定清空「{row.Name}」吗？" + Environment.NewLine + Environment.NewLine +
            $"这会删除该组里的 {count:N0} 条记录。" + Environment.NewLine +
            "删除前会自动备份数据库（backup/ 目录）。" + Environment.NewLine +
            "删除后数据由 Ditto 空闲时清理，除备份外无法恢复。" + Environment.NewLine + Environment.NewLine +
            "如果只是想解除归类，请用「组内条目移到未归类」。",
            "清空分组", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (ok != MessageBoxResult.OK) return;

        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) { _vm.StatusText = "写入通道未就绪"; return; }

        try
        {
            _vm.StatusText = $"正在清空「{row.Name}」…";
            var n = await writer.DeleteClipsInGroupAsync(row.Id);
            _vm.StatusText = $"已清空「{row.Name}」，删除 {n} 条";
            InvalidateCountsCache();
            await _vm.LoadAsync();
        }
        catch (Exception ex) { _vm.StatusText = "清空失败：" + ex.Message; }
    }

    /// <summary>把组内条目移回未归类（可逆）。</summary>
    private async void GroupMoveAllToUngrouped_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null || !row.IsRealGroup) return;

        var count = row.Count;
        if (count == 0) { _vm.StatusText = "这个组是空的"; return; }

        var ok = MessageBox.Show(this,
            $"把「{row.Name}」里的 {count:N0} 条移回未归类吗？" + Environment.NewLine + Environment.NewLine +
            "这是可逆操作，之后还能重新归类。",
            "移到未归类", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ok != MessageBoxResult.OK) return;

        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) { _vm.StatusText = "写入通道未就绪"; return; }

        try
        {
            var n = await writer.MoveAllToUngroupedAsync(row.Id);
            _vm.StatusText = $"已把 {n} 条移回未归类";
            InvalidateCountsCache();
            await _vm.LoadAsync();
        }
        catch (Exception ex) { _vm.StatusText = "操作失败：" + ex.Message; }
    }


    private async void GroupMoveTop_Click(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (g is null || g.Id <= 0) return;

        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;

        var r = await svc.ReparentAsync(g.Id, -1);
        _vm.StatusText = r.Message;
        if (r.Success) await _vm.LoadAsync();
    }

    private async void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (g is null || g.Id <= 0) { _vm.StatusText = "虚拟视图不能删除"; return; }

        var ok = MessageBox.Show(this,
            "确定删除分组「" + g.Name + "」吗？" + Environment.NewLine + Environment.NewLine
            + "组内条目会回到「未归类」，内容不会丢失。",
            "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;

        var r = await svc.DeleteAsync(g.Id);
        _vm.StatusText = r.Message;
        if (r.Success) await _vm.LoadAsync();
    }

    /// <summary>把预览里改好的内容复制到剪贴板（不改 Ditto 数据库）。</summary>
    private void SaveEdited_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = PreviewTextBox?.Text ?? _vm.PreviewText;
            if (string.IsNullOrEmpty(text)) { _vm.StatusText = "没有可保存的内容"; return; }
            System.Windows.Clipboard.SetText(text);
            _vm.StatusText = "已复制到剪贴板（原条目未修改）";
        }
        catch (Exception ex) { _vm.StatusText = "复制失败：" + ex.Message; }
    }

    /// <summary>把 HTML 交给 HtmlPanel 渲染（真富文本）。</summary>
    private void RenderRichPreview()
    {
        try
        {
            if (!_vm.PreviewIsRich || string.IsNullOrEmpty(_vm.PreviewHtml))
                return;

            RichPreview.Text = _vm.PreviewHtml;
        }
        catch (Exception ex)
        {
            _vm.StatusText = "富文本渲染失败：" + ex.Message;
        }
    }

    private void MoveToGroup_Click(object sender, RoutedEventArgs e)
    {
        _vm.StatusText = "移到分组：请用右键菜单，或在分组浮层里操作";
    }

    /// <summary>切换隐私条目的打码 / 原文（只是显示开关，不改隐私标记）。</summary>
    private void TogglePrivateRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null) return;
        _vm.Selected.ShowRawText = !_vm.Selected.ShowRawText;
        _vm.StatusText = _vm.Selected.ShowRawText
            ? "已临时显示原文（仍标记为隐私，AI 不可见）"
            : "已重新打码";
    }

    private async void TogglePrivate_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.Selected;
        if (row is null) return;

        row.IsPrivate = !row.IsPrivate;
        _vm.StatusText = row.IsPrivate
            ? $"已标记 #{row.Id} 为隐私（AI 不会读取）"
            : $"已取消 #{row.Id} 的隐私标记";
        await PersistPrivacyAsync(row);
    }

    private void AiOrganize_Click(object sender, RoutedEventArgs e)
    {
        var w = new AiOrganizeWindow { Owner = this };
        w.ShowDialog();
        _ = _vm.LoadAsync();
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null) return;
        var tag = TextInputDialog.Prompt(this, "添加标签（不含 #，不能有空格）");
        if (string.IsNullOrWhiteSpace(tag)) return;
        _vm.AddTagCommand.Execute(tag);
    }

    private void TagChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string tag })
        {
            _vm.RemoveTagCommand.Execute(tag);
            e.Handled = true;
        }
    }

    private void PreviewImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.OpenImageViewerCommand.Execute(null);
        e.Handled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app && app.WindowsManager is not null)
            app.WindowsManager.ShowSettings();
    }

    private void Stats_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app && app.WindowsManager is not null)
            app.WindowsManager.ShowStats();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsShuttingDown) return;   // 退出流程中允许真正关闭
        e.Cancel = true;
        Hide();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        _vm.IsDark = ThemeManager.Current == "dark";
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>从任意节点向上遍历，判断是否落在 ListBoxItem（分组行）里。</summary>
    private static bool IsOnGroupRow(System.Windows.DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.ListBoxItem) return true;
            if (node is System.Windows.Controls.ListBox) return false;

            node = node is System.Windows.Media.Visual
                   or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
