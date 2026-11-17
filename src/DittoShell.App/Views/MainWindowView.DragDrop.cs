using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

/// <summary>
/// 分组区拖动：拖动排序 + 拖动移入。
/// 排序偏好存 sidecar（不动 Ditto 数据）；移入走 lParentID（Ditto 原生字段）。
/// </summary>
public partial class MainWindowView
{
    private Point _dragStart;
    private GroupRowVM? _dragGroup;
    private bool _dragging;

    // ══════════════════════════════════════════
    // 拖动开始：记录起点
    // ══════════════════════════════════════════

    private void GroupsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(GroupsList);
        _dragGroup = FindGroupRow(e.OriginalSource as DependencyObject);
        _dragging = false;
    }

    // ══════════════════════════════════════════
    // 拖动进行：超过阈值启动 DragDrop
    // ══════════════════════════════════════════

    private void GroupsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragGroup is null || _dragging) return;

        var now = e.GetPosition(GroupsList);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragging = true;

        try
        {
            var data = new DataObject("DittoShell.GroupId", _dragGroup.Id);
            data.SetData("DittoShell.GroupName", _dragGroup.Name);
            DragDrop.DoDragDrop(GroupsList, data, DragDropEffects.Move);
        }
        catch { }
        finally
        {
            _dragging = false;
            _dragGroup = null;
        }
    }

    // ══════════════════════════════════════════
    // 拖动放置：分组 → 分组（排序）
    // ══════════════════════════════════════════

    private GroupRowVM? _lastDropTarget;

    /// <summary>
    /// 用 InputHitTest 找鼠标下的分组行。
    /// DragOver/Drop 的 e.OriginalSource 是被拖放的控件（ListBox），不是鼠标下的元素，
    /// 所以必须自己做命中测试。
    /// </summary>
    private GroupRowVM? GetRowAt(Point ptInGroupsList)
    {
        try
        {
            var hit = GroupsList.InputHitTest(ptInGroupsList) as DependencyObject;
            return FindGroupRow(hit);
        }
        catch { return null; }
    }

    /// <summary>取鼠标在 GroupsList 坐标系里的点。</summary>
    private Point GetPosInGroupsList(DragEventArgs e)
    {
        try { return e.GetPosition(GroupsList); }
        catch { return new Point(-1, -1); }
    }

    private void GroupsList_DragOver(object sender, DragEventArgs e)
    {
        bool isGroup = e.Data.GetDataPresent("DittoShell.GroupId");
        bool isClip = e.Data.GetDataPresent("DittoShell.ClipIds");

        if (!isGroup && !isClip)
        {
            e.Effects = DragDropEffects.None;
            ClearDropVisual();
            e.Handled = true;
            return;
        }

        // 找鼠标下的分组行（含子组）
        var target = GetRowAt(GetPosInGroupsList(e));

        if (target is null || !target.IsRealGroup)
        {
            ClearDropVisual();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 计算放置位置
        var pos = GetRowPosition(e, target);

        // 拖分组：只能上下插入（不能移入内部，避免环路）
        if (isGroup)
        {
            pos = pos == 3 ? 1 : pos;   // 中间区域当"上方插入"
        }

        // 更新高亮
        if (_lastDropTarget is not null && _lastDropTarget != target)
        {
            _lastDropTarget.IsDropTarget = false;
            _lastDropTarget.DropPosition = 0;
        }

        target.IsDropTarget = true;
        target.DropPosition = pos;
        _lastDropTarget = target;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>根据鼠标在行内的相对位置判断：1=上 2=下 3=中间（移入）。</summary>
    /// <summary>根据鼠标在行内的相对位置判断：1=上 2=下 3=中间（移入）。</summary>
    private int GetRowPosition(DragEventArgs e, GroupRowVM target)
    {
        // 找该行的实际容器（Border 里 DataContext 是 target）
        var node = GetRowContainer(target);
        if (node is null) return 3;

        var pt = e.GetPosition(node);
        var h = node.ActualHeight;
        if (h <= 0) return 3;

        var ratio = pt.Y / h;
        if (ratio < 0.28) return 1;      // 上 28% → 插入到上方
        if (ratio > 0.72) return 2;      // 下 28% → 插入到下方
        return 3;                        // 中间 44% → 移入内部
    }

    /// <summary>从视觉树里找到某个 GroupRowVM 对应的容器元素。</summary>
    private FrameworkElement? GetRowContainer(GroupRowVM target)
    {
        try
        {
            var item = GroupsList.ItemContainerGenerator
                .ContainerFromItem(target) as ListBoxItem;
            return item;
        }
        catch { return null; }
    }

    private void ClearDropVisual()
    {
        if (_lastDropTarget is not null)
        {
            _lastDropTarget.IsDropTarget = false;
            _lastDropTarget.DropPosition = 0;
            _lastDropTarget = null;
        }
    }

    private async void GroupsList_Drop(object sender, DragEventArgs e)
    {
        var target = GetRowAt(GetPosInGroupsList(e));

        // 取放置位置（用拖拽过程中记下的）
        var pos = target?.DropPosition ?? 3;

        ClearDropVisual();

        if (target is null || !target.IsRealGroup) return;

        // ── 情况 1：拖的是分组 → 调整顺序 ──
        if (e.Data.GetDataPresent("DittoShell.GroupId"))
        {
            var srcId = (long)e.Data.GetData("DittoShell.GroupId")!;
            if (srcId == target.Id) { e.Handled = true; return; }

            try
            {
                // pos: 1=插到前面 2=插到后面
                _vm.ReorderGroup(srcId, target.Id, afterTarget: pos == 2);
            }
            catch (Exception ex) { _vm.StatusText = "排序失败：" + ex.Message; }

            e.Handled = true;
            return;
        }

        // ── 情况 2：拖的是条目 → 移入分组 ──
        if (e.Data.GetDataPresent("DittoShell.ClipIds"))
        {
            var ids = e.Data.GetData("DittoShell.ClipIds") as long[];
            if (ids is null || ids.Length == 0) return;

            var svc = App.ServiceProvider?.GetService(
                typeof(DittoShell.Application.Services.GroupService))
                as DittoShell.Application.Services.GroupService;
            if (svc is null) return;

            try
            {
                _vm.StatusText = $"正在移入「{target.Name}」…";
                var r = await svc.MoveClipsAsync(ids, target.Id);
                _vm.StatusText = r.Message;

                // 从列表移除已移走的条目（不重建，避免卡顿）
                var moved = _vm.Items.Where(x => ids.Contains(x.Id)).ToList();
                foreach (var m in moved) _vm.Items.Remove(m);
                _vm.SelectedClips.Clear();

                await _vm.RefreshGroupCountsAsync();
            }
            catch (Exception ex) { _vm.StatusText = "移入失败：" + ex.Message; }

            e.Handled = true;
        }
    }

    // ══════════════════════════════════════════
    // 条目列表：拖动开始
    // ══════════════════════════════════════════

    private Point _clipDragStart;
    private ClipRowVM? _clipDragRow;
    private bool _clipDragging;

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _clipDragStart = e.GetPosition(ItemsList);
        _clipDragRow = FindClipRow(e.OriginalSource as DependencyObject);
        _clipDragging = false;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_clipDragRow is null || _clipDragging) return;

        var now = e.GetPosition(ItemsList);
        if (Math.Abs(now.X - _clipDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _clipDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _clipDragging = true;

        try
        {
            // 拖的可能是多选集合
            var ids = _vm.SelectedIdSnapshot();
            if (ids.Count == 0) ids = new List<long> { _clipDragRow.Id };

            var data = new DataObject("DittoShell.ClipIds", ids.ToArray());
            data.SetData("DittoShell.ClipCount", ids.Count);

            DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Move);
        }
        catch { }
        finally
        {
            _clipDragging = false;
            _clipDragRow = null;
        }
    }

    // ══════════════════════════════════════════
    // 辅助：从点击位置找分组行 / 条目行
    // ══════════════════════════════════════════

    private static GroupRowVM? FindGroupRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is GroupRowVM g)
                return g;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static ClipRowVM? FindClipRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is ClipRowVM c)
                return c;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
