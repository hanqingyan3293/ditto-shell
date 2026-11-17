using System.IO;
using System.Windows;
using System.Windows.Input;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

public partial class MainWindowView
{
    // ══════════════════════════════════════════════
    // 分组区扩展功能（7 项）
    // ══════════════════════════════════════════════

    /// <summary>① 新建子分组。</summary>
    private async void GroupNewChild_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null || !row.IsRealGroup) return;

        await CreateGroupAsync(row.Id);
    }

    /// <summary>② 复制组名到剪贴板。</summary>
    private void GroupCopyName_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null) return;

        try
        {
            Clipboard.SetText(row.Name);
            _vm.StatusText = $"已复制组名「{row.Name}」";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "复制失败：" + ex.Message;
        }
    }

    /// <summary>③ 只搜这个组（把组作为过滤条件写入搜索框）。</summary>
    private void GroupSearchInside_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null || !row.IsRealGroup) return;

        // 切到该组视图，焦点给搜索框
        _vm.SelectViewCommand.Execute(row);
        _vm.StatusText = $"已在「{row.Name}」内搜索，输入关键词";

        // 聚焦搜索框
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var search = FindName("SearchBox") as System.Windows.Controls.TextBox
                         ?? FindName("TopSearchBox") as System.Windows.Controls.TextBox;
            search?.Focus();
            search?.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>④ 导出这个组（文本 / JSON 两种格式，让用户选）。</summary>
    private async void GroupExport_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as GroupRowVM;
        if (row is null || !row.IsRealGroup) return;

        if (row.Count == 0) { _vm.StatusText = "这个组是空的"; return; }

        // 选格式
        var choice = MessageBox.Show(
            $"导出「{row.Name}」的 {row.Count} 条内容。" + Environment.NewLine + Environment.NewLine +
            "是 = JSON（结构化，适合程序处理）" + Environment.NewLine +
            "否 = 纯文本（适合阅读）" + Environment.NewLine + Environment.NewLine +
            "取消 = 不导出",
            "导出分组", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;
        var asJson = choice == MessageBoxResult.Yes;

        var svc = App.ServiceProvider?.GetService(
            typeof(DittoShell.Application.Services.GroupOpsService))
            as DittoShell.Application.Services.GroupOpsService;
        if (svc is null) { _vm.StatusText = "导出服务未就绪"; return; }

        try
        {
            _vm.StatusText = "正在导出…";
            var (ok, msg, content) = await svc.ExportAsync(row.Id, asJson);

            if (!ok) { _vm.StatusText = msg; return; }

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出分组",
                FileName = $"{SanitizeFileName(row.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.{(asJson ? "json" : "txt")}",
                Filter = asJson ? "JSON 文件|*.json|所有文件|*.*" : "文本文件|*.txt|所有文件|*.*",
                DefaultExt = asJson ? ".json" : ".txt"
            };

            if (save.ShowDialog(this) == true)
            {
                await File.WriteAllTextAsync(save.FileName, content,
                    new System.Text.UTF8Encoding(true));
                _vm.StatusText = $"{msg} → {Path.GetFileName(save.FileName)}";
            }
            else
            {
                _vm.StatusText = "已取消导出";
            }
        }
        catch (Exception ex)
        {
            _vm.StatusText = "导出失败：" + ex.Message;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length > 40 ? result[..40] : result;
    }
}
