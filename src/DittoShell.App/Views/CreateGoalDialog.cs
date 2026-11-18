using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Application.Services;

namespace DittoShell.App.Views;

/// <summary>创建目标对话框。</summary>
public sealed class CreateGoalDialog : Window
{
    public string Objective { get; private set; } = "";
    public int MaxRounds { get; private set; } = 30;

    public CreateGoalDialog()
    {
        Title = "创建目标";
        Width = 560;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        var title = new TextBlock
        {
            Text = "🎯 创建目标",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var desc = new TextBlock
        {
            Text = "AI 会分多轮自动执行，每轮做完检查是否达成，未达成继续。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(desc, 1);
        grid.Children.Add(desc);

        // 目标输入
        var objLabel = new TextBlock
        {
            Text = "你要 AI 完成什么？",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(objLabel, 2);
        grid.Children.Add(objLabel);

        var objBox = new TextBox
        {
            Height = 100,
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(objBox, 3);
        grid.Children.Add(objBox);

        // 示例
        var examples = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        examples.Children.Add(new TextBlock
        {
            Text = "示例：",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        void AddExample(string text)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(8, 3, 8, 3),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) => objBox.Text = text;
            examples.Children.Add(btn);
        }

        AddExample("把所有重复的条目清理掉，每组只保留最新的");
        AddExample("把未归类的条目全部整理到合适的组里");
        examples.Children.Add(new TextBlock());   // 占位
        Grid.SetRow(examples, 4);
        grid.Children.Add(examples);

        // 轮次 + 按钮
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var roundsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        roundsPanel.Children.Add(new TextBlock
        {
            Text = "最大轮次",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var roundsBox = new TextBox
        {
            Text = "30",
            Width = 60,
            Height = 28,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        roundsPanel.Children.Add(roundsBox);
        roundsPanel.Children.Add(new TextBlock
        {
            Text = "（每轮最多改 200 条）",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });
        Grid.SetColumn(roundsPanel, 0);
        bottom.Children.Add(roundsPanel);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 32, IsCancel = true };
        var okBtn = new Button
        {
            Content = "创建",
            Width = 88,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };
        okBtn.Click += (_, _) =>
        {
            var text = objBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, "请填写目标", "提示");
                return;
            }
            if (!int.TryParse(roundsBox.Text, out var r) || r < 1 || r > 200)
            {
                MessageBox.Show(this, "轮次请填 1-200", "提示");
                return;
            }
            Objective = text;
            MaxRounds = r;
            DialogResult = true;
        };
        btns.Children.Add(cancelBtn);
        btns.Children.Add(okBtn);
        Grid.SetColumn(btns, 2);
        bottom.Children.Add(btns);

        Grid.SetRow(bottom, 5);
        grid.Children.Add(bottom);

        Content = grid;
        Loaded += (_, _) => objBox.Focus();
    }
}
