using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>标签管理对话框：列出所有标签，可逐个删除。</summary>
public sealed class TagManagerDialog : Window
{
    public TagManagerDialog(
        IReadOnlyList<(string Name, int Count)> tags, ITagStore store)
    {
        Title = "标签管理";
        Width = 520;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        var title = new TextBlock
        {
            Text = $"标签管理（共 {tags.Count} 个）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        // 搜索
        var searchBox = new TextBox
        {
            Height = 30,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 10),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(searchBox, 1);
        grid.Children.Add(searchBox);

        // 列表
        var listPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        // 底部
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var closeBtn = new Button { Content = "关闭", Width = 90, Height = 30, IsCancel = true };
        footer.Children.Add(closeBtn);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        Content = grid;

        // 渲染列表
        void Render(string filter)
        {
            listPanel.Children.Clear();

            var filtered = string.IsNullOrWhiteSpace(filter)
                ? tags.OrderByDescending(t => t.Count).ThenBy(t => t.Name)
                : tags.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(t => t.Count).ThenBy(t => t.Name);

            int shown = 0;
            foreach (var (name, count) in filtered)
            {
                if (shown++ >= 300) break;   // 最多显示 300 条

                var row = new Grid { Height = 32, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Margin = new Thickness(4, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = count > 0 ? new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF))
                                     : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var nameText = new TextBlock
                {
                    Text = name,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = count > 0
                        ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
                        : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(nameText);

                var countText = new TextBlock
                {
                    Text = count > 0 ? count.ToString() : "空",
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
                };
                Grid.SetColumn(countText, 2);
                row.Children.Add(countText);

                var delBtn = new Button
                {
                    Content = "删除",
                    Width = 50,
                    Height = 24,
                    FontSize = 11,
                    Tag = name
                };
                delBtn.Click += async (s2, e2) =>
                {
                    if (s2 is not Button b) return;
                    var tagName = b.Tag as string ?? "";

                    var ok = MessageBox.Show(this,
                        $"删除标签「{tagName}」？" + Environment.NewLine + Environment.NewLine +
                        $"有 {count} 条内容用了它。删除后这些条目会失去这个标签。" +
                        Environment.NewLine + "（不影响条目本身）",
                        "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ok != MessageBoxResult.OK) return;

                    try
                    {
                        await store.DeleteTagAsync(tagName);
                        listPanel.Children.Remove(row);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "删除失败：" + ex.Message, "错误");
                    }
                };
                Grid.SetColumn(delBtn, 3);
                row.Children.Add(delBtn);

                listPanel.Children.Add(row);
            }

            if (shown == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "没有匹配的标签",
                    FontSize = 12,
                    Margin = new Thickness(0, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
                });
            }
        }

        Render("");
        searchBox.TextChanged += (_, _) => Render(searchBox.Text);
    }
}
