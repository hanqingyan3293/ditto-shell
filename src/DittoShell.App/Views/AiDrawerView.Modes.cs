using DittoShell.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.Views;

/// <summary>
/// AI 抽屉的三模式集成：
/// 权限切换 / 计划模式 / 目标模式
/// </summary>
public partial class AiDrawerView
{
    private PermissionSelector? _permissionSel;
    private Button? _planBtn;
    private Button? _goalBtn;
    private GoalStrip? _goalStrip;
    private bool _planMode;
    private AgentGoalService? _goalSvc;
    private AiTools? _aiTools;

    private void InitModes()
    {
        try
        {
            _aiTools = App.ServiceProvider?.GetService(typeof(AiTools)) as AiTools;
            _goalSvc = App.ServiceProvider?.GetService(typeof(AgentGoalService)) as AgentGoalService;

            // 权限选择器（输入框下方，照抄 DSH）
            _permissionSel = new PermissionSelector();
            if (PermissionHost is not null) PermissionHost.Content = _permissionSel;
            _permissionSel.LevelChanged += OnPermissionChanged;

            // 计划模式开关（小按钮）
            _planBtn = new Button
            {
                Content = "📋 计划",
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _planBtn.Click += (_, _) => TogglePlanMode();
            if (PlanBtnHost is not null) PlanBtnHost.Content = _planBtn;

            // 目标按钮
            _goalBtn = new Button
            {
                Content = "🎯 目标",
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _goalBtn.Click += (_, _) => OnGoalClicked();
            if (GoalBtnHost is not null) GoalBtnHost.Content = _goalBtn;

            // 创建目标条带（照抄 DSH）
            _goalStrip = new GoalStrip();
            if (GoalStripHost is not null)
                GoalStripHost.Content = _goalStrip;

            _goalStrip.EditClicked += () => OnGoalEdit();
            _goalStrip.ClearClicked += () => _ = OnGoalClearAsync();
            _goalStrip.PauseResumeClicked += () => _ = OnGoalPauseResumeAsync();

            // 初始权限
            ApplyPermission(_permissionSel.Current);

            // 目标事件
            if (_goalSvc is not null)
            {
                _goalSvc.GoalUpdated += OnGoalUpdated;
                _goalSvc.ProgressChanged += msg => Dispatcher.Invoke(() =>
                    UpdateGoalStrip());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("InitModes failed: " + ex.Message);
        }
    }

    // ═══════ 权限 ═══════

    private void OnPermissionChanged(AiPermissionLevel level)
        => ApplyPermission(level);

    private void ApplyPermission(AiPermissionLevel level)
    {
        if (_aiTools is not null) _aiTools.Permission = level;

        // 切到只读时自动关掉计划模式之外的东西
        if (level == AiPermissionLevel.ReadOnly && _planMode)
        {
            // 计划模式本来就只读，保持
        }
    }

    // ═══════ 计划模式 ═══════

    private void OnPlanModeToggled(bool on)
    {
        if (_aiTools is not null) _aiTools.PlanMode = on;

        // 计划模式时强制只读
        if (on)
        {
            _aiTools!.Permission = AiPermissionLevel.ReadOnly;
            if (_permissionSel is not null)
                _permissionSel.Current = AiPermissionLevel.ReadOnly;
        }
        else
        {
            _aiTools!.Permission = _permissionSel?.Current ?? AiPermissionLevel.Safe;
        }
    }

    // ═══════ 目标模式 ═══════

    private async void OnGoalClicked()
    {
        if (_goalSvc is null) { ShowHint("目标服务未就绪"); return; }

        // 已有进行中的目标 → 显示控制
        if (_goalSvc.Current is { Status: GoalStatus.Active or GoalStatus.Paused })
        {
            ShowGoalControls();
            return;
        }

        // 检查会话（目标模式必须在抽屉里用）
        var session = (DataContext as AiChatViewModel)?.CurrentSession;
        if (session is null)
        {
            ShowHint("请先在下方输入框发一条消息，建立会话后再创建目标");
            return;
        }

        // 创建新目标
        var dlg = new CreateGoalDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 绑定会话（必须在 CreateAsync 之前）
            if (!_goalSvc.AttachSession(session))
            {
                ShowHint("会话绑定失败，请重试");
                return;
            }

            var goal = await _goalSvc.CreateAsync(dlg.Objective, dlg.MaxRounds);

            // 同步给工具层
            if (_aiTools is not null) _aiTools.ActiveGoal = goal;

            SetBusy(true);
            ShowHint($"🎯 目标已创建：{goal.Objective}");

            // 启动自动续跑
            _ = _goalSvc.RunAsync(g => Dispatcher.Invoke(() =>
            {
                // 每轮开始前把最新目标同步给工具层（get_goal 要用）
                if (_aiTools is not null) _aiTools.ActiveGoal = g;
                ShowGoalHint(
                    $"第 {g.Round}/{g.MaxRounds} 轮 · {g.ProcessedItems} 条");
            }));
        }
        catch (Exception ex) { ShowHint("创建失败：" + ex.Message); }
    }

    private void ShowGoalControls()
    {
        var goal = _goalSvc?.Current;
        if (goal is null) return;

        var menu = new ContextMenu
        {
            PlacementTarget = _goalBtn ?? (UIElement)this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        var pauseItem = new MenuItem
        {
            Header = goal.Status == GoalStatus.Paused ? "继续" : "暂停",
            Tag = goal.Status == GoalStatus.Paused ? "resume" : "pause"
        };
        var stopItem = new MenuItem { Header = "放弃目标", Tag = "stop" };
        var infoItem = new MenuItem
        {
            Header = $"第 {goal.Round}/{goal.MaxRounds} 轮 · {goal.ProcessedItems} 条",
            IsEnabled = false
        };

        async void OnClick(object s, RoutedEventArgs e)
        {
            if (s is not MenuItem mi) return;
            try
            {
                switch (mi.Tag as string)
                {
                    case "pause": await _goalSvc!.PauseAsync(); break;
                    case "resume": await _goalSvc!.ResumeAsync(); break;
                    case "stop":
                        _goalSvc!.Cancel();
                        await _goalSvc.ClearAsync();
                        SetBusy(false);
                        ShowGoalHint(null);
                        ShowHint("目标已放弃");
                        break;
                }
            }
            catch (Exception ex) { ShowHint("操作失败：" + ex.Message); }
        }

        pauseItem.Click += OnClick;
        stopItem.Click += OnClick;

        menu.Items.Add(infoItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pauseItem);
        menu.Items.Add(stopItem);
        menu.IsOpen = true;
    }

    private void OnGoalUpdated(AgentGoal goal)
    {
        Dispatcher.Invoke(() =>
        {
            // 同步条带
            _goalStrip?.Bind(goal);
            switch (goal.Status)
            {
                case GoalStatus.Completed:
                    SetBusy(false);
                    ShowGoalHint("✓ 目标已完成");
                    ShowHint("✓ 目标已完成：" + goal.Summary);
                    break;

                case GoalStatus.Blocked:
                    SetBusy(false);
                    ShowGoalHint("⚠ 目标受阻");
                    ShowHint("⚠ 目标受阻：" + goal.Summary);
                    break;

                case GoalStatus.Cleared:
                    SetBusy(false);
                    ShowGoalHint(null);
                    break;

                case GoalStatus.Active:
                    ShowGoalHint(
                        $"第 {goal.Round}/{goal.MaxRounds} 轮 · {goal.ProcessedItems} 条");
                    break;
            }
        });
    }

    /// <summary>
    /// 恢复上次未完成的目标（重启后调用）。
    /// 自动绑定当前会话，让目标能继续跑。
    /// </summary>
    public async Task RestoreGoalAsync()
    {
        if (_goalSvc is null) return;

        try
        {
            var goal = await _goalSvc.LoadLatestAsync();
            if (goal is null)
            {
                ShowGoalHint(null);
                return;
            }

            // 已经是完成/清除的，不恢复
            if (goal.Status is GoalStatus.Completed or GoalStatus.Cleared)
            {
                ShowGoalHint(null);
                return;
            }

            // 绑定当前会话（必须有）
            var session = (DataContext as AiChatViewModel)?.CurrentSession;
            if (session is null)
            {
                ShowGoalHint(
                    $"⚠ 目标「{Truncate(goal.Objective, 20)}」待继续（发条消息后恢复）");
                ShowHint("有未完成的目标，发一条消息后可以继续");
                return;
            }

            if (!_goalSvc.AttachSession(session)) return;

            // 恢复状态
            await _goalSvc.RestoreAsync(goal);
            ShowGoalHint(
                $"{goal.Round}/{goal.MaxRounds} 轮 · {goal.ProcessedItems} 条（已恢复）");

            ShowHint($"已恢复目标：{Truncate(goal.Objective, 30)}");

            // 如果是 Active 状态，自动继续跑
            if (goal.Status == GoalStatus.Active)
            {
                SetBusy(true);
                _ = _goalSvc.RunAsync(g => Dispatcher.Invoke(() =>
                {
                    ShowGoalHint(
                        $"第 {g.Round}/{g.MaxRounds} 轮 · {g.ProcessedItems} 条");
                }));
            }
        }
        catch (Exception ex)
        {
            ShowHint("恢复目标失败：" + ex.Message);
        }
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ═══════ 辅助 ═══════

    private void ShowHint(string text)
    {
        try
        {
            if (DataContext is AiChatViewModel vm) vm.StatusText = text;
        }
        catch { }
    }

    // ═══════ 目标条带动作 ═══════

    private void OnGoalEdit()
    {
        var goal = _goalSvc?.Current;
        if (goal is null) return;

        var dlg = new InputDialog("编辑目标", goal.Objective)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return;

        goal.Objective = dlg.Value;
        _ = _goalSvc?.RestoreAsync(goal);
        _goalStrip?.Bind(goal);
        ShowHint("目标已更新");
    }

    private async System.Threading.Tasks.Task OnGoalClearAsync()
    {
        if (_goalSvc?.Current is null) return;

        var ok = MessageBox.Show(
            "清除这个目标？" + Environment.NewLine + Environment.NewLine +
            "不会撤销已经做的修改，只是停止继续跑。",
            "清除目标", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        _goalSvc.Cancel();
        await _goalSvc.ClearAsync();
        _goalStrip?.Bind(null);
        SetBusy(false);
        ShowHint("目标已清除");
    }

    private async System.Threading.Tasks.Task OnGoalPauseResumeAsync()
    {
        var goal = _goalSvc?.Current;
        if (goal is null) return;

        if (goal.Status == GoalStatus.Paused)
        {
            await _goalSvc!.ResumeAsync();
            SetBusy(true);
            _ = _goalSvc.RunAsync(g => Dispatcher.Invoke(() =>
            {
                if (_aiTools is not null) _aiTools.ActiveGoal = g;
                _goalStrip?.Bind(g);
            }));
        }
        else
        {
            await _goalSvc!.PauseAsync();
            SetBusy(false);
        }

        _goalStrip?.Bind(_goalSvc.Current);
    }

    // ═══════ 新 UI 辅助 ═══════

    private void TogglePlanMode()
    {
        _planMode = !_planMode;
        SetPlanMode(_planMode);
    }

    private void SetPlanMode(bool on)
    {
        _planMode = on;

        if (_aiTools is not null) _aiTools.PlanMode = on;

        if (on)
        {
            // 计划模式强制只读
            _aiTools!.Permission = AiPermissionLevel.ReadOnly;
            if (_permissionSel is not null) _permissionSel.Current = AiPermissionLevel.ReadOnly;
        }
        else
        {
            _aiTools!.Permission = _permissionSel?.Current ?? AiPermissionLevel.Safe;
        }

        if (_planBtn is not null)
        {
            if (on)
            {
                _planBtn.Content = "📋 计划中";
                _planBtn.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF));
                _planBtn.Foreground = Brushes.White;
            }
            else
            {
                _planBtn.Content = "📋 计划";
                _planBtn.Background = Brushes.Transparent;
                _planBtn.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void UpdateGoalStrip()
    {
        _goalStrip?.Bind(_goalSvc?.Current);
    }

    private void ShowGoalHint(string text)
    {
        // 目标进度显示在条带里，这里只更新 hint
    }

    private void SetBusy(bool busy)
    {
        if (_permissionSel is not null) _permissionSel.IsEnabled = !busy;
        if (_planBtn is not null) _planBtn.IsEnabled = !busy;

        if (_goalBtn is not null)
        {
            _goalBtn.IsEnabled = !busy;
            _goalBtn.Content = busy ? "🎯 进行中" : "🎯 目标";
        }
    }
}
