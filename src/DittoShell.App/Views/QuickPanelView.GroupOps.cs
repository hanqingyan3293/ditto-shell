using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

public partial class QuickPanelView : Window
{
    // ───────── 分组右键菜单（B3）─────────

    private GroupRowVM? GroupFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.Parent is not ContextMenu cm) return null;
        return (cm.PlacementTarget as FrameworkElement)?.Tag as GroupRowVM
               ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as GroupRowVM;
    }

    private void Toast(string message, bool success)
    {
        var vm = (QuickPanelViewModel)DataContext;
        vm.FootHint = message;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            vm.FootHint = "";
        };
        timer.Start();
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        _ = CreateGroup(parentId: -1, parentName: null);
    }

    private void GroupCtx_NewSub(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (!EnsureRealGroup(g)) return;
        _ = CreateGroup(g!.Id, g.Name);
    }

    private async Task CreateGroup(long parentId, string? parentName)
    {
        var title = parentName is null ? "新建分组" : $"在「{parentName}」下新建子组";
        var name = TextInputDialog.Prompt(this, title);
        if (name is null) return;

        var vm = (QuickPanelViewModel)DataContext;
        var r = await vm.CreateGroupAsync(name, parentId);
        Toast(r.Message, r.Success);
    }

    private async void GroupCtx_Rename(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (!EnsureRealGroup(g)) return;

        var name = TextInputDialog.Prompt(this, "重命名分组", g!.Name);
        if (name is null || name == g.Name) return;

        var vm = (QuickPanelViewModel)DataContext;
        var r = await vm.RenameGroupAsync(g.Id, name);
        Toast(r.Message, r.Success);
    }

    private async void GroupCtx_Delete(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (!EnsureRealGroup(g)) return;

        var confirm = MessageBox.Show(this,
            $"确定删除分组「{g!.Name}」吗？\n\n组内条目会回到「未归类」，内容不会丢失。",
            "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var vm = (QuickPanelViewModel)DataContext;
        var r = await vm.DeleteGroupAsync(g.Id);
        Toast(r.Message, r.Success);
    }

    private async void GroupCtx_MoveTop(object sender, RoutedEventArgs e)
    {
        var g = GroupFromMenu(sender);
        if (!EnsureRealGroup(g)) return;

        var vm = (QuickPanelViewModel)DataContext;
        var r = await vm.MoveGroupToTopAsync(g!.Id);
        Toast(r.Message, r.Success);
    }

    private bool EnsureRealGroup(GroupRowVM? g)
    {
        if (g is null) return false;
        if (g.Id <= 0)
        {
            Toast("这是虚拟视图，不能修改", false);
            return false;
        }
        return true;
    }
}
