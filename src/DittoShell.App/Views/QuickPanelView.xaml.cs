using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

public partial class QuickPanelView : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    public QuickPanelView(QuickPanelViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // 无边框窗口的边缘拖拽缩放
        Services.WindowResizeHelper.Enable(this, border: 6);

        vm.RequestClose = Hide;
        vm.RequestManage = () =>
        {
            Hide();
            if (System.Windows.Application.Current is App app && app.WindowsManager is not null)
                app.WindowsManager.SwitchToMainFromQuick();
        };

        _hoverTimer.Tick += OnHoverTick;

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
    }


    // ───────── 悬停预览 ─────────

    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer =
        new() { Interval = TimeSpan.FromMilliseconds(600) };
    private ClipRowVM? _hoverRow;


    /// <summary>所有右键菜单打开时统一定位（转发给 App 的全局处理器）。</summary>
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        DittoShell.App.App.MenuOpened(sender, e);
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
            var vm = (QuickPanelViewModel)DataContext;
            var detail = await vm.GetDetailAsync(row.Id);
            if (detail is null || _hoverRow != row) return;

            System.Windows.Media.Imaging.BitmapSource? img = null;
            if (row.IsImage)
            {
                var dib = await vm.GetDibAsync(row.Id);
                if (dib is not null) img = await Task.Run(() => Media.DibDecoder.Decode(dib, 640));
            }
            if (_hoverRow != row) return;

            HoverCard.Show(detail, img, row.KindBrush, row.KindLabel, row.TimeText);
            PlaceHoverNearCursor();
            HoverPop.IsOpen = true;
        }
        catch { }
    }

    /// <summary>显示在鼠标附近，并保证不超出当前屏幕工作区。</summary>
    public void ShowNearCaret()
    {
        if (!GetCursorPos(out var pt))
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 3;
            Show();
            return;
        }

        var source = PresentationSource.FromVisual(this);
        double scaleX = 1, scaleY = 1;
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformFromDevice.M11;
            scaleY = source.CompositionTarget.TransformFromDevice.M22;
        }

        double mx = pt.X * scaleX;
        double my = pt.Y * scaleY;

        double w = Width, h = Height;
        double left = mx - w / 2;
        double top = my + 18;

        // 用鼠标所在屏幕的工作区做约束
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenW = SystemParameters.VirtualScreenWidth;
        var screenH = SystemParameters.VirtualScreenHeight;

        if (left < screenLeft + 8) left = screenLeft + 8;
        if (left + w > screenLeft + screenW - 8) left = screenLeft + screenW - w - 8;
        if (top + h > screenTop + screenH - 48)
            top = my - h - 18;
        if (top < screenTop + 8) top = screenTop + 8;

        Left = left;
        Top = top;
        Show();
    }

    /// <summary>隐藏面板后等焦点回到原窗口，再让 Ditto 粘贴。</summary>
    private async Task PasteAfterHideAsync(long clipId, bool plainText)
    {
        await Task.Delay(120);
        var vm = (QuickPanelViewModel)DataContext;
        var ok = await vm.PasteAsync(clipId, plainText);
        if (!ok) System.Diagnostics.Debug.WriteLine("paste failed");
    }

    /// <summary>Tab 展开/收起右侧分组栏（面板整体变宽，不遮挡内容）。</summary>
    /// <summary>展开/收起右侧分组栏（会让面板整体变宽）。</summary>
    public void UpdateGroupWidth(bool open)
    {
        GroupColumn.Width = open ? new GridLength(168) : new GridLength(0);
        Width = open ? 548 : 380;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void GroupArrow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupRowVM vm })
        {
            if (DataContext is QuickPanelViewModel pvm)
            {
                pvm.ToggleGroupExpandCommand.Execute(vm);
                e.Handled = true;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosRawQP(out QP_POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct QP_POINT { public int X; public int Y; }

    /// <summary>让右键菜单出现在鼠标右下（Absolute + 屏幕坐标）。</summary>
    private void ClipRow_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
    }

    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
    }

    private void ClipMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (DataContext is not QuickPanelViewModel vm || vm.Selected is null) return;

        var row = vm.Selected;

        switch (mi.Tag as string)
        {
            case "paste":
                Hide();
                _ = PasteAfterHideAsync(row.Id, false);
                break;
            case "plain":
                Hide();
                _ = PasteAfterHideAsync(row.Id, true);
                break;
            case "privacy":
                vm.TogglePrivate(row);
                break;
            case "copy":
                try
                {
                    System.Windows.Clipboard.SetText(row.Preview);
                    vm.FootHint = "已复制到剪贴板";
                }
                catch { vm.FootHint = "复制失败"; }
                break;
            case "detail":
                vm.IsDetailOpen = true;
                UpdateDetailWidth(true);
                break;
            case "image":
                vm.IsDetailOpen = true;
                UpdateDetailWidth(true);
                break;
            case "movegroup":
                MoveToGroupFromQuick(vm);
                break;
            case "ungroup":
                _ = MoveToTopLevelFromQuick(vm);
                break;
            case "delete":
                DeleteFromQuick(vm);
                break;
            case "copyhtml":
            case "copyimage":
                vm.FootHint = "请在详情列里查看该格式的内容";
                break;
            case "filterby":
                if (vm.Selected is not null)
                {
                    var t = vm.Selected.Preview ?? "";
                    vm.SearchText = t.Length > 40 ? t[..40] : t;
                    vm.FootHint = "已按该条内容过滤";
                }
                break;
            case "newitem":
            case "edit":
                vm.IsDetailOpen = true;
                UpdateDetailWidth(true);
                vm.FootHint = "在右侧详情里编辑";
                break;
            case "props":
                if (vm.Selected is not null)
                {
                    var r = vm.Selected;
                    MessageBox.Show(
                        $"ID：{r.Id}{Environment.NewLine}类型：{r.Kind}"
                        + $"{Environment.NewLine}时间：{r.CreatedAt:yyyy-MM-dd HH:mm:ss}"
                        + $"{Environment.NewLine}隐私：{(r.IsPrivate ? "是" : "否")}",
                        "属性", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                break;
            case "keep":
            case "unkeep":
                _ = SetKeepFromQuick(vm, mi.Tag as string == "keep");
                break;
            case "sticky":
            case "unsticky":
                _ = SetStickyFromQuick(vm, mi.Tag as string == "sticky");
                break;
            case "opensource":
                vm.FootHint = "来源程序请在主界面右键打开";
                break;
            case "exporttxt":
            case "exportjson":
                vm.FootHint = "导出请在主界面右键操作";
                break;
        }
    }

    // ───────── 右键菜单的异步操作 ─────────

    private void MoveToGroupFromQuick(QuickPanelViewModel vm)
    {
        if (vm.Selected is null) return;
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) { vm.FootHint = "分组服务未就绪"; return; }

        var picker = new GroupPickerWindow { Owner = this, GroupList = vm.Groups.ToList() };
        if (picker.ShowDialog() != true || picker.SelectedGroupId is null) return;

        _ = Task.Run(async () =>
        {
            var r = await svc.MoveClipsAsync(new[] { vm.Selected!.Id }, picker.SelectedGroupId.Value);
            Dispatcher.Invoke(() => vm.FootHint = r.Message);
        });
    }

    private async Task MoveToTopLevelFromQuick(QuickPanelViewModel vm)
    {
        if (vm.Selected is null) return;
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.GroupService))
                  as DittoShell.Application.Services.GroupService;
        if (svc is null) return;
        var r = await svc.MoveClipsAsync(new[] { vm.Selected.Id }, 0);
        vm.FootHint = r.Message;
    }

    private void DeleteFromQuick(QuickPanelViewModel vm)
    {
        if (vm.Selected is null) return;

        var ok = MessageBox.Show(this,
            "确定删除这一条吗？删除前已自动备份。",
            "删除条目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) { vm.FootHint = "写入通道未就绪"; return; }

        var id = vm.Selected.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await writer.DeleteClipAsync(id);
                Dispatcher.Invoke(() =>
                {
                    vm.FootHint = $"已删除 #{id}";
                    vm.Reload();
                });
            }
            catch (Exception ex) { Dispatcher.Invoke(() => vm.FootHint = "删除失败：" + ex.Message); }
        });
    }

    private async Task SetKeepFromQuick(QuickPanelViewModel vm, bool keep)
    {
        if (vm.Selected is null) return;
        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) return;
        try
        {
            await writer.SetKeepAsync(vm.Selected.Id, keep);
            vm.FootHint = keep ? "已设为禁止自动删除" : "已允许自动删除";
        }
        catch (Exception ex) { vm.FootHint = "操作失败：" + ex.Message; }
    }

    private async Task SetStickyFromQuick(QuickPanelViewModel vm, bool sticky)
    {
        if (vm.Selected is null) return;
        var writer = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.DittoDb.DittoDbWriter))
                     as DittoShell.Infrastructure.DittoDb.DittoDbWriter;
        if (writer is null) return;
        try
        {
            await writer.SetStickyAsync(vm.Selected.Id, sticky);
            vm.FootHint = sticky ? "已置顶" : "已取消置顶";
        }
        catch (Exception ex) { vm.FootHint = "操作失败：" + ex.Message; }
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QuickPanelViewModel vm) return;
        vm.ToggleGroupCommand.Execute(null);
        UpdateGroupWidth(vm.IsGroupOpen);
    }

    /// <summary>悬停卡片放在鼠标右下角（与主界面一致）。</summary>
    private void PlaceHoverNearCursor()
    {
        if (!GetCursorPos(out var p)) return;

        var source = PresentationSource.FromVisual(this);
        double sx = 1, sy = 1;
        if (source?.CompositionTarget is not null)
        {
            sx = source.CompositionTarget.TransformFromDevice.M11;
            sy = source.CompositionTarget.TransformFromDevice.M22;
        }

        double mx = p.X * sx, my = p.Y * sy;
        const double cardW = 420;
        double cardH = 380;

        double x = mx + 16, y = my + 16;
        double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (x + cardW > right - 8) x = mx - cardW - 16;
        if (y + cardH > bottom - 8) y = my - cardH - 16;
        if (x < SystemParameters.VirtualScreenLeft + 8) x = SystemParameters.VirtualScreenLeft + 8;
        if (y < SystemParameters.VirtualScreenTop + 8) y = SystemParameters.VirtualScreenTop + 8;

        HoverPop.HorizontalOffset = x;
        HoverPop.VerticalOffset = y;
    }

    /// <summary>展开/收起右侧详情列。</summary>
    public void UpdateDetailWidth(bool open)
    {
        DetailDivider.Width = open ? new GridLength(4) : new GridLength(0);
        DetailColumn.Width = open ? new GridLength(320) : new GridLength(0);
        Width = 380 + (GroupColumn.Width.Value > 0 ? 168 : 0) + (open ? 324 : 0);
    }

    private void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QuickPanelViewModel vm || vm.Selected is null) return;
        try
        {
            System.Windows.Clipboard.SetText(vm.Selected.Preview);
            vm.FootHint = "已复制到剪贴板";
        }
        catch { vm.FootHint = "复制失败"; }
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // 故意不关闭：只有 Esc 或右上角 X 才关（用户明确要求）
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (QuickPanelViewModel)DataContext;

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.IsSlashOpen) { vm.IsSlashOpen = false; e.Handled = true; }
                else if (vm.IsGroupOpen) { vm.IsGroupOpen = false; UpdateGroupWidth(false); e.Handled = true; }
                else Hide();
                e.Handled = true;
                break;

            case Key.Right:
                if (DataContext is QuickPanelViewModel rvm)
                {
                    rvm.IsDetailOpen = true;
                    UpdateDetailWidth(true);
                    e.Handled = true;
                }
                break;

            case Key.Left:
                if (DataContext is QuickPanelViewModel lvm)
                {
                    lvm.IsDetailOpen = false;
                    UpdateDetailWidth(false);
                    e.Handled = true;
                }
                break;

            case Key.Tab:
                vm.ToggleGroupCommand.Execute(null);
                UpdateGroupWidth(vm.IsGroupOpen);
                e.Handled = true;
                break;

            case Key.Down:
                ItemsList.SelectedIndex = Math.Min(ItemsList.SelectedIndex + 1, ItemsList.Items.Count - 1);
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                ItemsList.SelectedIndex = Math.Max(ItemsList.SelectedIndex - 1, 0);
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
            {
                var sel = vm.Selected;
                if (sel is not null)
                {
                    bool plain = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                    Hide();
                    _ = PasteAfterHideAsync(sel.Id, plain);
                }
                e.Handled = true;
                break;
            }

            case >= Key.D1 and <= Key.D9:
            {
                int idx = e.Key - Key.D1;
                if (idx < vm.Items.Count)
                {
                    ItemsList.SelectedIndex = idx;
                    Hide();
                    _ = PasteAfterHideAsync(vm.Items[idx].Id, false);
                }
                e.Handled = true;
                break;
            }
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not null) Hide();
    }

    private void GroupOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            ((QuickPanelViewModel)DataContext).IsGroupOpen = false;
    }

    private void SlashOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            ((QuickPanelViewModel)DataContext).IsSlashOpen = false;
    }

    private void GroupItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupRowVM g })
        {
            ((QuickPanelViewModel)DataContext).SelectGroupCommand.Execute(g);
            e.Handled = true;
        }
    }

    private void FilterBarOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is QuickPanelViewModel vm) vm.IsFilterBarOpen = false;
    }

    private void FilterChoice_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilterChoiceVM c }
            && DataContext is QuickPanelViewModel vm)
        {
            vm.ApplyFilterChoice(c);
            e.Handled = true;
        }
    }

    private void SlashItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlashItemVM item })
        {
            ((QuickPanelViewModel)DataContext).RunSlash(item);
            e.Handled = true;
        }
    }
}
