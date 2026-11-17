using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>嵌入主界面的统计页。加载时从 Ditto.db 取真实数据。</summary>
public partial class StatsPage : UserControl
{
    public event EventHandler? BackRequested;

    public StatsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async Task LoadAsync()
    {
        var reader = App.ServiceProvider?.GetService(typeof(IClipReader)) as IClipReader;
        if (reader is null) return;

        try
        {
            var counts = await reader.GetViewCountsAsync();
            TotalText.Text = counts.GetValueOrDefault("total").ToString("N0");
            TodayText.Text = counts.GetValueOrDefault("today").ToString("N0");

            long bytes = reader.GetDatabaseSizeBytes();
            SizeText.Text = bytes >= 1024L * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
                : $"{bytes / 1024.0 / 1024:F0} MB";

            // 图片占库比例（粗略：图片条目数 / 总数 只是参考，改用固定说明）
            var images = counts.GetValueOrDefault("images");
            var total = Math.Max(1, counts.GetValueOrDefault("total"));
            SizeHint.Text = $"图片条目 {images:N0} · 占总数 {images * 100.0 / total:F1}%";

            var daily = await reader.GetDailyCountsAsync(14);
            BuildBars(daily);

            // 分组分布
            var dist = await reader.GetGroupDistributionAsync(10);
            int maxDist = Math.Max(1, dist.Count == 0 ? 1 : dist.Max(d => d.Count));
            GroupDistList.ItemsSource = dist.Select(d => new
            {
                d.Name,
                CountText = $"{d.Count:N0} 条",
                BarWidth = Math.Max(4.0, 120.0 * d.Count / maxDist)
            }).ToList();

            // 来源应用
            var sidecar = App.ServiceProvider?.GetService(typeof(DittoShell.Infrastructure.Sidecar.SidecarStore))
                          as DittoShell.Infrastructure.Sidecar.SidecarStore;
            if (sidecar is not null)
            {
                var src = await sidecar.GetSourceDistributionAsync(10);
                SourceDistList.ItemsSource = src.Select(x => new
                {
                    Name = x.App.Replace(".exe", ""),
                    CountText = $"{x.Count} 条"
                }).ToList();
            }

            // 标签使用
            var tagStore = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                           as DittoShell.Domain.Abstractions.ITagStore;
            if (tagStore is not null)
            {
                var tags = await tagStore.GetAllTagsAsync();
                TagDistList.ItemsSource = tags
                    .Where(t => t.Name != "__private")
                    .Take(10)
                    .Select(t => new { t.Name, CountText = $"{t.Count} 条" })
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            SizeHint.Text = "读取失败：" + ex.Message;
        }
    }

    private void BuildBars(IReadOnlyList<(string Day, int Count)> data)
    {
        int max = Math.Max(1, data.Count == 0 ? 1 : data.Max(d => d.Count));
        var list = new List<object>();

        // 补齐 14 天空缺的日期
        var map = data.ToDictionary(d => d.Day, d => d.Count);
        for (int i = 13; i >= 0; i--)
        {
            var day = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
            int n = map.GetValueOrDefault(day);
            double h = Math.Max(4, 138.0 * n / max);

            var brush = i == 0
                ? new SolidColorBrush(Color.FromRgb(22, 119, 255))
                : new SolidColorBrush(Color.FromArgb((byte)(70 + 120.0 * n / max), 22, 119, 255));
            brush.Freeze();

            list.Add(new
            {
                Height = h,
                Brush = brush,
                Tip = $"{day[5..]} · {n} 条"
            });
        }

        DailyBars.ItemsSource = list;
    }
}
