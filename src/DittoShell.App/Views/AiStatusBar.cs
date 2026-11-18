using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>
/// AI 抽屉的状态栏：权限 + 计划模式 + 目标进度。
/// </summary>
public sealed class AiStatusBar : System.Windows.Controls.Border
{
    private readonly PermissionSelector _permission;
    private readonly System.Windows.Controls.Button _planBtn;
    private readonly System.Windows.Controls.Button _goalBtn;
    private readonly System.Windows.Controls.TextBlock _goalText;
    private readonly System.Windows.Controls.TextBlock _hintText;

    public event Action<AiPermissionLevel>? PermissionChanged;
    public event Action<bool>? PlanModeToggled;
    public event Action? GoalClicked;

    private bool _planMode;

    public AiPermissionLevel Permission => _permission.Current;
    public bool IsPlanMode => _planMode;

    public AiStatusBar()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        Padding = new System.Windows.Thickness(12, 4, 12, 4);
        Height = 34;

        var grid = new System.Windows.Controls.Grid();

        // 左：权限 + 计划 + 目标
        var left = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        _permission = new PermissionSelector();
        _permission.LevelChanged += lv => PermissionChanged?.Invoke(lv);
        left.Children.Add(_permission);

        _planBtn = new System.Windows.Controls.Button
        {
            Content = "📋 计划",
            Height = 26,
            Padding = new System.Windows.Thickness(9, 0, 9, 0),
            FontSize = 11,
            Margin = new System.Windows.Thickness(6, 0, 0, 0),
            ToolTip = "开启后 AI 先探索出方案，你批准后才执行"
        };
        _planBtn.Click += (_, _) => TogglePlan();
        left.Children.Add(_planBtn);

        _goalBtn = new System.Windows.Controls.Button
        {
            Content = "🎯 目标",
            Height = 26,
            Padding = new System.Windows.Thickness(9, 0, 9, 0),
            FontSize = 11,
            Margin = new System.Windows.Thickness(6, 0, 0, 0),
            ToolTip = "创建一个长期目标，AI 自动多轮执行直到完成"
        };
        _goalBtn.Click += (_, _) => GoalClicked?.Invoke();
        left.Children.Add(_goalBtn);

        _goalText = new System.Windows.Controls.TextBlock
        {
            FontSize = 10.5,
            Margin = new System.Windows.Thickness(8, 0, 0, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Visibility = System.Windows.Visibility.Collapsed
        };
        left.Children.Add(_goalText);

        System.Windows.Controls.Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // 右：提示
        _hintText = new System.Windows.Controls.TextBlock
        {
            FontSize = 10.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        };
        System.Windows.Controls.Grid.SetColumn(_hintText, 1);
        grid.Children.Add(_hintText);

        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = System.Windows.GridLength.Auto
        });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });

        Child = grid;

        ApplyStyle();
        UpdateHint();
    }

    private void ApplyStyle()
    {
        if (TryFindResource("ChipButton") is System.Windows.Style s)
        {
            _planBtn.Style = s;
            _goalBtn.Style = s;
        }

        _hintText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, "B.Text3");
        _goalText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, "B.Primary");
    }

    private void TogglePlan()
    {
        _planMode = !_planMode;
        UpdatePlanButton();
        UpdateHint();
        PlanModeToggled?.Invoke(_planMode);
    }

    private void UpdatePlanButton()
    {
        _planBtn.Content = _planMode ? "📋 计划中" : "📋 计划";

        if (_planMode)
        {
            _planBtn.SetResourceReference(
                System.Windows.Controls.Control.BackgroundProperty, "B.Primary");
            _planBtn.SetResourceReference(
                System.Windows.Controls.Control.ForegroundProperty, "White");
        }
        else
        {
            _planBtn.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            _planBtn.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
    }

    private void UpdateHint()
    {
        _hintText.Text = _planMode
            ? "计划模式：AI 先出方案，你批准后执行"
            : "";
    }

    public void SetPlanMode(bool on)
    {
        if (_planMode == on) return;
        _planMode = on;
        UpdatePlanButton();
        UpdateHint();
    }

    public void SetPermission(AiPermissionLevel level)
        => _permission.Current = level;

    /// <summary>更新目标进度显示。</summary>
    public void SetGoalProgress(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _goalText.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            _goalText.Text = text;
            _goalText.Visibility = System.Windows.Visibility.Visible;
        }
    }

    /// <summary>目标进行中时禁用权限和计划切换。</summary>
    public void SetBusy(bool busy)
    {
        _permission.IsEnabled = !busy;
        _planBtn.IsEnabled = !busy;
        _goalBtn.IsEnabled = !busy;
        _goalBtn.Content = busy ? "🎯 进行中" : "🎯 目标";
    }
}
