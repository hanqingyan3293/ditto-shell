using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DittoShell.App.Controls;
using DittoShell.App.Services;

namespace DittoShell.App.Views;

/// <summary>嵌入主界面的设置页（7 个分页）。</summary>
public partial class SettingsPage : UserControl
{
    /// <summary>点「返回」时通知宿主切回剪贴板页。</summary>
    public event EventHandler? BackRequested;

    public SettingsPage()
    {
        InitializeComponent();
        NavList.SelectedIndex = 0;
        BuildProgress();
        InitDittoPage();
        InitAiPage();
        InitHotkeyPage();
        InitSidebarPage();
        InitBackupPage();
        InitDensity();
    }

    private void InitDittoPage()
    {
        DittoShell.Application.Services.SettingsService? svc = null;
        try
        {
            svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.SettingsService))
                as DittoShell.Application.Services.SettingsService;
        }
        catch { }

        var vm = new ViewModels.DittoSettingsVM(svc);
        PageDitto.DataContext = vm;
        DittoList.ItemsSource = vm.Rows;

        void SyncStatus() => DittoStatus.Text = vm.Status;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.DittoSettingsVM.Status)) SyncStatus();
        };
        SyncStatus();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // ───────── 外观：列表密度 ─────────

    private void InitDensity()
    {
        var d = App.Config.ListDensity;
        if (d < 40) d = 40;
        if (d > 96) d = 96;
        DensitySlider.Value = d;
        DensityValue.Text = d.ToString();
    }

    private void Density_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DensityValue is null) return;
        var v = (int)Math.Round(e.NewValue);
        DensityValue.Text = v.ToString();

        if (App.Config.ListDensity == v) return;
        App.Config.ListDensity = v;
        App.SaveConfig();

        // 立刻应用到主窗口列表
        App.NotifyDensityChanged(v);
    }

    // ───────── 快捷键录制 ─────────

    private bool _recordingHotkey;

    private void InitHotkeyPage()
    {
        HotkeyQuickBox.Text = App.Config.QuickPanelHotkey;
        HotkeyMainBox.Text = App.Config.MainWindowHotkey;
    }

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _recordingHotkey = true;
        if (sender is TextBox tb) tb.Text = "按下组合键…";
    }

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _recordingHotkey = false;
        // 没录到就恢复原值
        if (sender == HotkeyQuickBox && HotkeyQuickBox.Text.Contains("按下"))
            HotkeyQuickBox.Text = App.Config.QuickPanelHotkey;
        if (sender == HotkeyMainBox && HotkeyMainBox.Text.Contains("按下"))
            HotkeyMainBox.Text = App.Config.MainWindowHotkey;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey || sender is not TextBox tb) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        // Delete / Backspace = 不设快捷键
        if (key is Key.Delete or Key.Back)
        {
            tb.Text = "（未设置）";
            var isQuickClear = tb == HotkeyQuickBox;
            ApplyHotkey(isQuickClear, "");
            return;
        }

        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            HotkeyStatus.Text = "必须包含 Ctrl / Alt / Shift / Win 中的至少一个";
            return;
        }

        parts.Add(key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            >= Key.F1 and <= Key.F12 => key.ToString(),
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Space => "Space",
            _ => key.ToString()
        });

        var gesture = string.Join("+", parts);
        tb.Text = gesture;

        var isQuick = tb == HotkeyQuickBox;
        ApplyHotkey(isQuick, gesture);
    }

    private void ApplyHotkey(bool isQuick, string gesture)
    {
        var svc = App.HotkeySvc;
        if (svc is null) { HotkeyStatus.Text = "热键服务未启动"; return; }

        var ok = isQuick ? svc.RegisterQuickPanel(gesture) : svc.RegisterMainWindow(gesture);

        if (ok)
        {
            if (isQuick) App.Config.QuickPanelHotkey = gesture;
            else App.Config.MainWindowHotkey = gesture;
            App.SaveConfig();

            HotkeyStatus.Text = gesture.Length == 0 ? "已取消该快捷键（不占用）" : $"已生效：{gesture}";
            HotkeyStatus.Foreground = (System.Windows.Media.Brush)FindResource("B.Success");
        }
        else
        {
            HotkeyStatus.Text = svc.LastError ?? "注册失败";
            HotkeyStatus.Foreground = (System.Windows.Media.Brush)FindResource("B.Danger");
        }
    }

    // ───────── 备份页 ─────────

    private DittoShell.Infrastructure.Data.BackupService? _backupSvc;

    /// <summary>关于页里的链接点击。</summary>
    private void AboutLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe) return;
        var url = fe.Tag as string switch
        {
            "repo" => "https://github.com/hanqingyan3293/ditto-shell",
            "issues" => "https://github.com/hanqingyan3293/ditto-shell/issues",
            _ => null
        };
        if (url is null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void InitBackupPage()
    {
        _backupSvc = App.ServiceProvider?.GetService(
            typeof(DittoShell.Infrastructure.Data.BackupService))
            as DittoShell.Infrastructure.Data.BackupService;

        RefreshBackupList();
    }

    private void RefreshBackupList()
    {
        if (BackupList is null) return;

        if (_backupSvc is null)
        {
            BackupStatText.Text = "备份服务未初始化";
            return;
        }

        var files = _backupSvc.ListBackups();
        var items = files.Select(f => new BackupItemVM(f)).ToList();
        BackupList.ItemsSource = items;

        var totalMb = _backupSvc.TotalSizeBytes() / 1024.0 / 1024.0;
        BackupStatText.Text = files.Count == 0
            ? "还没有备份"
            : $"共 {files.Count} 份 · 占用 {totalMb:F0} MB";
        BackupDirText.Text = _backupSvc.BackupDirectory;

        BackupEmptyText.Visibility = files.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshBackup_Click(object sender, RoutedEventArgs e) => RefreshBackupList();

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BackupItemVM item }) return;
        if (_backupSvc is null) return;

        var ok = MessageBox.Show(
            $"确定删除这份备份吗？" + Environment.NewLine + Environment.NewLine +
            item.Name + Environment.NewLine + item.SizeText + Environment.NewLine + Environment.NewLine +
            "删除后无法恢复（但当前数据库不受影响）。",
            "删除备份", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        if (_backupSvc.DeleteBackup(item.Name))
        {
            RefreshBackupList();
            BackupStatText.Text += "（已删除 1 份）";
        }
    }

    private void KeepLatestBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_backupSvc is null) return;

        var ok = MessageBox.Show(
            "只保留最新 1 份备份，删除其余全部？" + Environment.NewLine + Environment.NewLine +
            "这会释放磁盘空间，但旧的回退点会消失。",
            "清理备份", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        var n = _backupSvc.KeepLatest(1);
        RefreshBackupList();
        MessageBox.Show($"已删除 {n} 份旧备份。", "完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ───────── 侧栏配置页 ─────────

    public System.Collections.ObjectModel.ObservableCollection<ViewModels.SidebarItemVM> SidebarItems { get; }
        = new();

    private Point _dragStart;
    private ViewModels.SidebarItemVM? _dragItem;

    private void InitSidebarPage()
    {
        SidebarItems.Clear();

        var cfg = App.Config.SidebarItems ?? new List<DittoShell.Domain.Abstractions.SidebarItemConfig>();
        var defaults = new (string Key, string Name)[]
        {
            ("all", "全部历史"), ("secrets", "凭据"), ("images", "图片"), ("links", "链接"),
            ("code", "代码"), ("files", "文件"), ("commands", "命令"), ("longtext", "长文本"),
            ("contacts", "邮箱/电话"), ("today", "今天"), ("uncategorized", "未归类")
        };

        var ordered = cfg.Count == 0
            ? defaults.Select(d => (d.Key, d.Name, Visible: true)).ToList()
            : cfg.Select(c =>
              {
                  var d = defaults.FirstOrDefault(x => x.Key == c.Key);
                  return (c.Key, d.Name ?? c.Key, c.Visible);
              }).ToList();

        foreach (var (key, name, visible) in ordered)
            SidebarItems.Add(new ViewModels.SidebarItemVM(key, name, visible, locked: key == "all")
            {
                Visible = key == "all" ? true : visible
            });

        SidebarItemList.ItemsSource = SidebarItems;
        SidebarStatus.Text = $"{SidebarItems.Count} 项";
    }

    private void SidebarHandle_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SidebarItemVM item })
        {
            _dragStart = e.GetPosition(this);
            _dragItem = item;
        }
    }

    private void SidebarHandle_Move(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.Y - _dragStart.Y) < 6) return;   // 阈值，避免误触

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, _dragItem, DragDropEffects.Move);
        }
        catch { }
        finally { _dragItem = null; }
    }

    /// <summary>拖到目标行的上半还是下半（决定插在前面还是后面）。</summary>
    private bool _insertAfter;

    private void SidebarRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ViewModels.SidebarItemVM))
            ? DragDropEffects.Move : DragDropEffects.None;

        if (sender is Border b)
        {
            var y = e.GetPosition(b).Y;
            _insertAfter = y > b.ActualHeight / 2;

            // 插入线：上半 → 顶部 2px；下半 → 底部 2px
            b.BorderThickness = _insertAfter
                ? new Thickness(0, 0, 0, 2)
                : new Thickness(0, 2, 0, 0);
        }

        e.Handled = true;
    }

    private void SidebarRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    private void SidebarRow_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);

        if (sender is not FrameworkElement { Tag: ViewModels.SidebarItemVM target }) return;
        if (e.Data.GetData(typeof(ViewModels.SidebarItemVM)) is not ViewModels.SidebarItemVM source) return;

        var from = SidebarItems.IndexOf(source);
        var targetIdx = SidebarItems.IndexOf(target);
        if (from < 0 || targetIdx < 0) return;

        // 插到目标行的前/后，再换算成 Move 的目标索引
        var to = _insertAfter ? targetIdx + 1 : targetIdx;
        if (from < to) to--;

        if (to < 0) to = 0;
        if (to >= SidebarItems.Count) to = SidebarItems.Count - 1;
        if (from == to) { e.Handled = true; return; }

        SidebarItems.Move(from, to);
        SaveSidebar();
        e.Handled = true;
    }

    private void ResetSidebar_Click(object sender, RoutedEventArgs e)
    {
        App.Config.SidebarItems = new List<DittoShell.Domain.Abstractions.SidebarItemConfig>();
        App.SaveConfig();
        InitSidebarPage();
        InitBackupPage();
        App.NotifySidebarChanged();
        SidebarStatus.Text = "已恢复默认";
    }

    private void SaveSidebar()
    {
        App.Config.SidebarItems = SidebarItems
            .Select(x => new DittoShell.Domain.Abstractions.SidebarItemConfig
            {
                Key = x.Key,
                Visible = x.Visible
            })
            .ToList();

        App.SaveConfig();
        App.NotifySidebarChanged();
        SidebarStatus.Text = "已保存（重启后仍生效）";
    }

    // ───────── 数据分页 ─────────

    private async void DataBackup_Click(object sender, RoutedEventArgs e)
    {
        var backup = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.IBackupService))
                     as DittoShell.Domain.Abstractions.IBackupService;
        if (backup is null || string.IsNullOrEmpty(App.DetectedDatabase))
        {
            DataStatusText.Text = "未找到 Ditto 数据库";
            return;
        }

        DataStatusText.Text = "正在备份（830MB 约需数秒）…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var name = await backup.BackupDbAsync(App.DetectedDatabase, force: true);
        sw.Stop();

        DataStatusText.Text = name is null
            ? "备份失败"
            : $"已备份到 backup/{name}（{sw.ElapsedMilliseconds} ms）";
    }

    private async void DataExport_Click(object sender, RoutedEventArgs e)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.ExportService))
                  as DittoShell.Application.Services.ExportService;
        if (svc is null) { DataStatusText.Text = "导出服务未初始化"; return; }
        if (string.IsNullOrEmpty(App.DetectedDatabase)) { DataStatusText.Text = "未找到 Ditto 数据库"; return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "生成迁移包",
            FileName = $"DittoShell_迁移包_{DateTime.Now:yyyyMMdd}.zip",
            Filter = "ZIP 压缩包|*.zip"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        DataStatusText.Text = "正在打包…";
        var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
        var r = await svc.CreateMigrationPackageAsync(dlg.FileName, App.DetectedDatabase, dataDir);
        DataStatusText.Text = r.Message;
    }

    private void DataCleanup_Click(object sender, RoutedEventArgs e)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.ExportService))
                  as DittoShell.Application.Services.ExportService;
        if (svc is null) return;

        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "backup");
        var n = svc.CleanupBackups(dir, keep: 5);
        DataStatusText.Text = n == 0 ? "没有可清理的旧备份（保留最近 5 份）" : $"已清理 {n} 个旧备份";
    }

    // ───────── AI 分页 ─────────

    private DittoShell.Domain.Abstractions.ICredentialStore? _creds;

    private void InitAiPage()
    {
        try
        {
            _creds = App.ServiceProvider?.GetService(typeof(DittoShell.Domain.Abstractions.ICredentialStore))
                     as DittoShell.Domain.Abstractions.ICredentialStore;

            var cfg = App.Config;
            AiEnabledToggle.IsChecked = cfg.AiEnabled;
            AiBaseUrlBox.Text = cfg.AiBaseUrl;
            AiModelCombo.ItemsSource = new List<string> { cfg.AiModel, "deepseek-flash", "deepseek-v4-pro" }
                .Distinct().ToList();
            AiModelCombo.SelectedItem = cfg.AiModel;

            if (AiBudgetCombo is not null)
            {
                // 找最接近当前配置的选项（容错旧配置）
                System.Windows.Controls.ComboBoxItem? best = null;
                long bestDiff = long.MaxValue;

                foreach (System.Windows.Controls.ComboBoxItem bi in AiBudgetCombo.Items)
                {
                    if (bi.Tag is not string t || !int.TryParse(t, out var bv)) continue;
                    var diff = Math.Abs((long)bv - cfg.AiContextBudget);
                    if (diff < bestDiff) { bestDiff = diff; best = bi; }
                }

                if (best is not null)
                {
                    AiBudgetCombo.SelectedItem = best;
                    if (bestDiff > 0) App.Config.AiContextBudget =
                        int.Parse((string)best.Tag);   // 顺手纠正旧值
                }
            }

            var key = _creds?.Read(cfg.AiKeyRef);
            AiStatusText.Text = string.IsNullOrEmpty(key) ? "尚未保存 API Key" : "已保存 Key（掩码显示）";
            if (!string.IsNullOrEmpty(key))
                AiKeyBox.Text = new string('•', Math.Min(24, key.Length));
        }
        catch (Exception ex)
        {
            AiStatusText.Text = "初始化失败：" + ex.Message;
        }
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.AiService))
                  as DittoShell.Application.Services.AiService;
        if (svc is null) { AiStatusText.Text = "AI 服务未初始化"; return; }

        AiStatusText.Text = "正在获取模型列表…";
        var (ok, models, error) = await svc.ListModelsAsync();

        if (!ok) { AiStatusText.Text = "获取失败：" + (error ?? "未知错误"); return; }

        AiModelCombo.ItemsSource = models;
        var cur = App.Config.AiModel;
        AiModelCombo.SelectedItem = models.Contains(cur) ? cur : models.FirstOrDefault();
        AiStatusText.Text = $"拉到 {models.Count} 个模型，当前：{App.Config.AiModel}";
    }

    private void AiBudget_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AiBudgetCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is string tag && int.TryParse(tag, out var v))
        {
            App.Config.AiContextBudget = v;
            App.SaveConfig();
            AiStatusText.Text = $"上下文预算已设为 {v / 1024}K";
        }
    }

    private void AiModel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AiModelCombo?.SelectedItem is string m && m.Length > 0)
        {
            App.Config.AiModel = m;
            App.SaveConfig();
            AiStatusText.Text = "模型已切换为 " + m;
        }
    }

    private void AiEnabled_Changed(object sender, RoutedEventArgs e)
    {
        App.Config.AiEnabled = AiEnabledToggle.IsChecked == true;
        App.SaveConfig();
        AiStatusText.Text = App.Config.AiEnabled ? "AI 已启用" : "AI 已关闭";
    }

    private void AiConfig_Changed(object sender, RoutedEventArgs e)
    {
        App.Config.AiBaseUrl = AiBaseUrlBox.Text.Trim();
        App.Config.AiModel = AiModelCombo.Text.Trim();
        App.SaveConfig();
        AiStatusText.Text = "已保存接口配置";
    }

    private void AiSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = AiKeyBox.Text.Trim();
        if (key.Length == 0)
        {
            AiStatusText.Text = "请先填写 API Key";
            return;
        }
        if (key.All(c => c == '•'))
        {
            AiStatusText.Text = "Key 未改动";
            return;
        }

        var ok = _creds?.Write(App.Config.AiKeyRef, key) == true;
        if (ok)
        {
            var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.AiService))
                      as DittoShell.Application.Services.AiService;
            AiKeyBox.Text = new string('•', Math.Min(24, key.Length));
            AiStatusText.Text = "Key 已保存到 Windows 凭据管理器";
        }
        else
        {
            AiStatusText.Text = "保存失败（凭据管理器写入被拒绝）";
        }
    }

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        var svc = App.ServiceProvider?.GetService(typeof(DittoShell.Application.Services.AiService))
                  as DittoShell.Application.Services.AiService;
        if (svc is null) { AiStatusText.Text = "AI 服务未初始化"; return; }

        AiStatusText.Text = "正在测试…";
        var (ok, msg, ms) = await svc.TestAsync();
        AiStatusText.Text = msg;
        AiStatusText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("B.Success")
            : (System.Windows.Media.Brush)FindResource("B.Danger");
    }

    /// <summary>按 NavList 的 Tag 切换分页。</summary>
    public void NavigateTo(string tag)
    {
        for (int i = 0; i < NavList.Items.Count; i++)
        {
            if ((NavList.Items[i] as ListBoxItem)?.Tag as string == tag)
            {
                NavList.SelectedIndex = i;
                return;
            }
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null) return;

        var tag = (NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "general";

        PageGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageAi.Visibility = tag == "ai" ? Visibility.Visible : Visibility.Collapsed;
        PageData.Visibility = tag == "data" ? Visibility.Visible : Visibility.Collapsed;
        if (PageSidebar is not null) PageSidebar.Visibility = tag == "sidebar" ? Visibility.Visible : Visibility.Collapsed;
        if (PageBackup is not null) PageBackup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
        PageDitto.Visibility = tag == "ditto" ? Visibility.Visible : Visibility.Collapsed;
        PageProgress.Visibility = tag == "progress" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo?.SelectedIndex is not int idx) return;
        ThemeManager.Apply(idx == 1 ? "dark" : "light");
    }

    // ───────── 开发进度页 ─────────

    private void BuildProgress()
    {
        var doc = ProgressService.Load();
        int total = doc.Batches.Sum(b => b.Total);
        int done = doc.Batches.Sum(b => b.Done);
        double pct = total == 0 ? 0 : (double)done / total * 100;

        PercentText.Text = $"{pct:F0}%";
        PercentSub.Text = $"{done} / {total} 项已完成 · 更新于 {doc.Updated}";

        ProgressList.Items.Clear();
        foreach (var batch in doc.Batches)
            ProgressList.Items.Add(BuildBatchCard(batch));
    }

    private UIElement BuildBatchCard(ProgressBatch b)
    {
        var border = new Border
        {
            Background = Res("B.CardBg") as Brush,
            BorderBrush = Res("B.Divider") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var root = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Background = b.AllDone ? new SolidColorBrush(Color.FromArgb(26, 82, 196, 26))
                                   : b.Started ? new SolidColorBrush(Color.FromArgb(26, 22, 119, 255))
                                   : new SolidColorBrush(Color.FromArgb(20, 140, 140, 150)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = b.Id,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = b.AllDone ? new SolidColorBrush(Color.FromRgb(82, 196, 26))
                            : b.Started ? new SolidColorBrush(Color.FromRgb(22, 119, 255))
                            : new SolidColorBrush(Color.FromRgb(140, 140, 150))
            }
        };
        Grid.SetColumn(badge, 0);
        head.Children.Add(badge);

        var name = new TextBlock
        {
            Text = b.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("B.Text") as Brush
        };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var count = new TextBlock
        {
            Text = $"{b.Done} / {b.Total}",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("B.Text3") as Brush
        };
        Grid.SetColumn(count, 2);
        head.Children.Add(count);

        root.Children.Add(head);

        var track = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = Res("B.Divider") as Brush,
            Margin = new Thickness(0, 9, 0, 9)
        };
        var fill = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = b.AllDone ? new SolidColorBrush(Color.FromRgb(82, 196, 26)) : Res("B.Primary") as Brush,
            Width = Math.Max(0, b.Percent * 260)
        };
        track.Child = fill;
        root.Children.Add(track);

        foreach (var item in b.Items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Icon
            {
                Kind = item.Done ? "Check" : "Circle",
                Width = 12,
                Height = 12,
                StrokeThickness = 2,
                Stroke = item.Done ? new SolidColorBrush(Color.FromRgb(82, 196, 26)) : Res("B.Text3") as Brush,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 11.5,
                Foreground = item.Done ? Res("B.Text2") as Brush : Res("B.Text3") as Brush,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(row);
        }

        border.Child = root;
        return border;
    }

    private static object? Res(string key) => System.Windows.Application.Current?.TryFindResource(key);
}

/// <summary>备份列表里的一项。</summary>
public sealed class BackupItemVM
{
    public BackupItemVM(System.IO.FileInfo f)
    {
        Name = f.Name;
        SizeBytes = f.Length;
        CreatedAt = f.CreationTime;
    }

    public string Name { get; }
    public long SizeBytes { get; }
    public DateTime CreatedAt { get; }

    public string SizeText => SizeBytes > 1024 * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
        : $"{SizeBytes / 1024.0 / 1024:F0} MB";

    public string MetaText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
}
