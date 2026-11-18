using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DittoShell.App.Views;

/// <summary>写操作确认对话框（安全模式用）。</summary>
public sealed class WriteConfirmDialog : Window
{
    public bool Approved { get; private set; }
    public bool RememberChoice { get; private set; }

    public WriteConfirmDialog(string toolName, string summary, string detail)
    {
        Title = "确认修改";
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        titlePanel.Children.Add(new TextBlock
        {
            Text = "⚠",
            FontSize = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xAD, 0x14))
        });
        titlePanel.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "AI 想修改数据",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = $"工具：{toolName}",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    Margin = new Thickness(0, 2, 0, 0)
                }
            }
        });
        Grid.SetRow(titlePanel, 0);
        grid.Children.Add(titlePanel);

        // 摘要
        var summaryBox = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 12)
        };
        summaryBox.Child = new TextBlock
        {
            Text = summary,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
        };
        Grid.SetRow(summaryBox, 1);
        grid.Children.Add(summaryBox);

        // 详情
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = new TextBox
        {
            Text = detail,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        // 底部
        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rememberBox = new CheckBox
        {
            Content = "本次会话不再询问这个工具",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
        };
        Grid.SetColumn(rememberBox, 0);
        bottom.Children.Add(rememberBox);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };

        var rejectBtn = new Button
        {
            Content = "拒绝",
            Width = 88,
            Height = 32,
            IsCancel = true
        };
        rejectBtn.Click += (_, _) =>
        {
            Approved = false;
            RememberChoice = rememberBox.IsChecked == true;
            Close();
        };

        var approveBtn = new Button
        {
            Content = "允许",
            Width = 88,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        approveBtn.Click += (_, _) =>
        {
            Approved = true;
            RememberChoice = rememberBox.IsChecked == true;
            Close();
        };

        btns.Children.Add(rejectBtn);
        btns.Children.Add(approveBtn);
        Grid.SetColumn(btns, 1);
        bottom.Children.Add(btns);

        Grid.SetRow(bottom, 3);
        grid.Children.Add(bottom);

        Content = grid;
    }
}
