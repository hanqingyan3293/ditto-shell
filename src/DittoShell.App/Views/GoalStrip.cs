using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>
/// 目标条带（照抄 DSH 的 composer context strip）。
/// 显示在输入框上方，紧凑一行：
///   🎯 目标文本…              第 3/30 轮 · 180 条  [编辑] [清除] [暂停]
/// </summary>
public sealed class GoalStrip : Border
{
    private readonly TextBlock _objectiveText;
    private readonly TextBlock _progressText;
    private readonly StackPanel _actions;
    private readonly Button _toggleBtn;

    public event Action? EditClicked;
    public event Action? ClearClicked;
    public event Action? PauseResumeClicked;

    private AgentGoal? _goal;

    public GoalStrip()
    {
        Height = 32;
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(10, 0, 8, 0);
        Margin = new Thickness(0, 0, 0, 6);
        Visibility = Visibility.Collapsed;

        Background = new SolidColorBrush(Color.FromArgb(0x0F, 0x16, 0x77, 0xFF));
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x16, 0x77, 0xFF));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 🎯
        var icon = new TextBlock
        {
            Text = "🎯",
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // 目标文本
        _objectiveText = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
        };
        Grid.SetColumn(_objectiveText, 1);
        grid.Children.Add(_objectiveText);

        // 进度
        _progressText = new TextBlock
        {
            FontSize = 10.5,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF))
        };
        Grid.SetColumn(_progressText, 2);
        grid.Children.Add(_progressText);

        // 动作按钮
        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _toggleBtn = MakeActionButton("暂停", () => PauseResumeClicked?.Invoke());
        var editBtn = MakeActionButton("编辑", () => EditClicked?.Invoke());
        var clearBtn = MakeActionButton("清除", () => ClearClicked?.Invoke());

        _actions.Children.Add(editBtn);
        _actions.Children.Add(clearBtn);
        _actions.Children.Add(_toggleBtn);

        Grid.SetColumn(_actions, 3);
        grid.Children.Add(_actions);

        Child = grid;
    }

    private Button MakeActionButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 10.5,
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x16, 0x77, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>绑定目标数据。</summary>
    public void Bind(AgentGoal? goal)
    {
        _goal = goal;

        if (goal is null || goal.Status is GoalStatus.Cleared or GoalStatus.Completed)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        _objectiveText.Text = goal.Objective;
        _objectiveText.ToolTip = goal.Objective;

        _progressText.Text = goal.Status switch
        {
            GoalStatus.Blocked => "受阻",
            GoalStatus.Paused => "已暂停",
            _ => $"第 {goal.Round}/{goal.MaxRounds} 轮 · {goal.ProcessedItems} 条"
        };

        _toggleBtn.Content = goal.Status == GoalStatus.Paused ? "继续" : "暂停";

        // 完成后隐藏动作
        _actions.Visibility = goal.Status switch
        {
            GoalStatus.Completed => Visibility.Collapsed,
            _ => Visibility.Visible
        };

        // 受阻时高亮
        if (goal.Status == GoalStatus.Blocked)
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFA, 0x8C, 0x16));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFA, 0x8C, 0x16));
            _progressText.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0x38, 0x0D));
        }
        else
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0x16, 0x77, 0xFF));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x16, 0x77, 0xFF));
            _progressText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF));
        }
    }
}
