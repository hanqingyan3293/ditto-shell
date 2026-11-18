using System.Windows;
using System.Windows.Controls;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

public partial class SettingsPage
{
    // ══════════════════════════════════════════
    // 标签管理
    // ══════════════════════════════════════════

    private async void RefreshTags_Click(object sender, RoutedEventArgs e)
        => await RefreshTagStatsAsync();

    private async Task RefreshTagStatsAsync()
    {
        var tags = App.ServiceProvider?.GetService(typeof(ITagStore)) as ITagStore;
        if (tags is null) { TagStatusText.Text = "标签服务未就绪"; return; }

        try
        {
            var all = await tags.GetAllTagsAsync();
            var withCounts = all.ToList();

            int total = withCounts.Count;
            int used = withCounts.Count(x => x.Count > 0);
            int orphan = total - used;

            TagTotalText.Text = total.ToString("N0");
            TagOrphanText.Text = orphan.ToString("N0");
            TagUsedText.Text = used.ToString("N0");

            PurgeOrphanBtn.IsEnabled = orphan > 0;
            TagStatusText.Text = orphan > 0
                ? $"有 {orphan} 个空标签可以清理"
                : "所有标签都有引用";
        }
        catch (Exception ex) { TagStatusText.Text = "读取失败：" + ex.Message; }
    }

    private async void PurgeOrphanTags_Click(object sender, RoutedEventArgs e)
    {
        var tags = App.ServiceProvider?.GetService(typeof(ITagStore)) as ITagStore;
        if (tags is null) return;

        var ok = MessageBox.Show(
            "清理没有任何条目引用的标签？" + Environment.NewLine + Environment.NewLine +
            "只会删除「空标签」，有引用的一定保留。" + Environment.NewLine +
            "不影响 Ditto 数据库，只动旁路库。",
            "清理空标签", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            PurgeOrphanBtn.IsEnabled = false;
            TagStatusText.Text = "正在清理…";

            var n = await tags.PurgeOrphanTagsAsync();
            TagStatusText.Text = $"✓ 已清理 {n} 个空标签";

            await RefreshTagStatsAsync();
            App.NotifyGroupsChanged();
        }
        catch (Exception ex)
        {
            TagStatusText.Text = "清理失败：" + ex.Message;
            PurgeOrphanBtn.IsEnabled = true;
        }
    }

    private async void ViewAllTags_Click(object sender, RoutedEventArgs e)
    {
        var tags = App.ServiceProvider?.GetService(typeof(ITagStore)) as ITagStore;
        if (tags is null) return;

        try
        {
            var all = await tags.GetAllTagsAsync();
            var list = all.ToList();

            var dlg = new TagManagerDialog(all, tags) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();

            await RefreshTagStatsAsync();
        }
        catch (Exception ex) { TagStatusText.Text = "打开失败：" + ex.Message; }
    }
}
