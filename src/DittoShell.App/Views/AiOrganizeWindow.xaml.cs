using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.App.Views;

/// <summary>
/// AI 整理窗口（三阶段：配置 → 审阅方案 → 按方案分类）。
/// </summary>
public partial class AiOrganizeWindow : Window
{
    private readonly IClipReader? _reader;
    private readonly AiService? _ai;
    private readonly OrganizeService? _organize;

    private readonly OrganizeSampler? _sampler;
    private readonly OrganizeAnalyzer? _analyzer;
    private readonly OrganizeRunner? _runner;

    private List<ClipItem> _candidates = new();
    private AnalyzeResult? _analysis;
    private TaxonomyScheme? _activeScheme;
    private List<OrganizeItem> _results = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    public ObservableCollection<TaxonomyGroup> SchemeGroups { get; } = new();
    public ObservableCollection<OrganizeItem> ResultItems { get; } = new();

    public AiOrganizeWindow()
    {
        InitializeComponent();

        _reader = App.ServiceProvider?.GetService(typeof(IClipReader)) as IClipReader;
        _ai = App.ServiceProvider?.GetService(typeof(AiService)) as AiService;
        _organize = App.ServiceProvider?.GetService(typeof(OrganizeService)) as OrganizeService;

        if (_reader is not null && _ai is not null && _organize is not null)
        {
            _sampler = new OrganizeSampler(_reader);
            _analyzer = new OrganizeAnalyzer(_reader, _ai, _sampler, _organize);
            _runner = new OrganizeRunner(_reader, _ai, _organize);
        }

        SchemeGroupList.ItemsSource = SchemeGroups;
        ResultList.ItemsSource = ResultItems;

        // 首次引导
        if (App.Config.OrganizeIntroSeen) IntroBox.Visibility = Visibility.Collapsed;

        Loaded += async (_, _) => await RefreshCountsAsync();
    }

    // ═══════ 初始化 ═══════

    private async Task RefreshCountsAsync()
    {
        if (_reader is null || _organize is null) return;

        try
        {
            // 取全部未归类（不限条数）
            var all = (await _reader.SearchAsync("", -1, null)).ToList();
            var (fresh, _) = await _organize.FilterNewAsync(all);
            _candidates = all;

            // 显示每批失败原因（如果有）
            if (_runner is not null && _runner.LastErrors.Count > 0)
                StatusText.Text = "上次失败：" + string.Join("；", _runner.LastErrors.Take(2));

            IncrementalDesc.Text = $"只处理新增的（当前 {fresh.Count} 条待整理）";
            FullDesc.Text = $"清空记录重新分所有未归类（{all.Count:N0} 条 · 约 {(all.Count / 1500.0):F1} 分钟 · 约 {(all.Count * 0.00007):F2} 元）";
            RetryDesc.Text = "只重试之前整理失败的";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    // ═══════ 交互 ═══════

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            var ok = MessageBox.Show(this, "正在整理中，确定关闭吗？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }
        Close();
    }

    private void DismissIntro_Click(object sender, RoutedEventArgs e)
    {
        IntroBox.Visibility = Visibility.Collapsed;
        App.Config.OrganizeIntroSeen = true;
        App.SaveConfig();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (StartBtnText is null) return;
        UpdateStartButton();
    }

    private void AnalyzeFirst_Changed(object sender, RoutedEventArgs e)
    {
        if (SkipHint is null) return;
        SkipHint.Visibility = AnalyzeFirstCheck.IsChecked == true
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        var mode = ModeFull.IsChecked == true ? "全部重来"
                 : ModeRetry.IsChecked == true ? "补漏重试"
                 : "增量整理";
        var analyze = AnalyzeFirstCheck.IsChecked == true;
        StartBtnText.Text = analyze ? $"开始分析并整理（{mode}）" : $"直接分类（{mode}）";
    }

    // ═══════ 开始 ═══════

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_analyzer is null || _runner is null || _reader is null)
        {
            StatusText.Text = "服务未初始化";
            return;
        }

        if (!_ai!.Enabled)
        {
            StatusText.Text = "AI 没启用（设置 → AI 填 Key 并打开开关）";
            return;
        }

        if (ModeFull.IsChecked == true)
        {
            var ok = MessageBox.Show(this,
                "「全部重来」会清空整理记录，重新处理所有未归类条目。" +
                Environment.NewLine + Environment.NewLine +
                $"约 {_candidates.Count:N0} 条，预计 {_candidates.Count / 1500.0:F0} 分钟，花费约 {_candidates.Count * 0.00007:F2} 元。" +
                Environment.NewLine + "确定继续吗？",
                "全部重来", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        var analyzeFirst = AnalyzeFirstCheck.IsChecked == true;
        var prompt = PromptBox?.Text?.Trim();

        if (analyzeFirst)
        {
            await RunAnalyzeAsync(prompt);
        }
        else
        {
            // 跳过分析：用默认方案
            _activeScheme = new TaxonomyScheme
            {
                Name = "自动分类",
                Groups = BuildDefaultGroups()
            };
            await RunClassifyAsync(prompt);
        }
    }

    private List<TaxonomyGroup> BuildDefaultGroups()
        => new()
        {
            new TaxonomyGroup { Index = 1, Path = "00 待清理", Reason = "无效/测试内容" },
        };

    // ═══════ 阶段 1：分析 ═══════

    private async Task RunAnalyzeAsync(string? prompt)
    {
        SetRunning(true, "正在采样 500 条…");
        ProgressArea.Visibility = Visibility.Visible;
        SchemeArea.Visibility = Visibility.Collapsed;
        ResultArea.Visibility = Visibility.Collapsed;

        _cts = new CancellationTokenSource();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _analyzer!.AnalyzeAsync(prompt, true, _cts.Token);
            sw.Stop();

            if (!result.Success)
            {
                StatusText.Text = "分析失败：" + (result.Error ?? "未知");
                return;
            }

            _analysis = result;
            ProfileText.Text = result.Profile;
            RecommendText.Text = string.IsNullOrWhiteSpace(result.Recommendation)
                ? "" : "💡 " + result.Recommendation;

            BuildSchemeTabs(result.Schemes);
            if (result.Schemes.Count > 0) SelectScheme(result.Schemes[0]);

            SchemeArea.Visibility = Visibility.Visible;
            StatusText.Text = $"分析完成：{result.Schemes.Count} 套方案，"
                + $"{(result.Tokens / 1000.0):F1}k tokens（约 {result.Cost:F3} 元），"
                + $"{sw.Elapsed.TotalSeconds:F1} 秒";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消"; }
        catch (Exception ex) { StatusText.Text = "分析异常：" + ex.Message; }
        finally
        {
            SetRunning(false, null);
            ProgressArea.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BuildSchemeTabs(List<TaxonomyScheme> schemes)
    {
        SchemeTabs.Children.Clear();

        for (int i = 0; i < schemes.Count; i++)
        {
            var s = schemes[i];
            var idx = i;
            var btn = new Button
            {
                Content = s.Name,
                Tag = idx,
                Height = 26,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("ChipButton")
            };
            btn.Click += (_, _) => SelectScheme(schemes[idx]);
            SchemeTabs.Children.Add(btn);
        }
    }

    private void SelectScheme(TaxonomyScheme scheme)
    {
        _activeScheme = scheme;
        SchemeGroups.Clear();
        foreach (var g in scheme.Groups) SchemeGroups.Add(g);

        // 高亮当前方案
        foreach (Button b in SchemeTabs.Children)
            b.Background = (int)b.Tag == scheme.Groups.Count
                ? (Brush)FindResource("B.Primary")
                : Brushes.Transparent;
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaxonomyGroup g }) return;

        var dlg = new InputDialog("改分组名", g.Path)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            g.Path = dlg.Value.Trim();
            SchemeGroups.Remove(g);
            SchemeGroups.Insert(0, g);   // 触发刷新
        }
    }

    private void Reanalyze_Click(object sender, RoutedEventArgs e)
        => _ = RunAnalyzeAsync(PromptBox?.Text?.Trim());

    // ═══════ 阶段 2：按方案分类 ═══════

    private async void RunScheme_Click(object sender, RoutedEventArgs e)
        => await RunClassifyAsync(PromptBox?.Text?.Trim());

    private async Task RunClassifyAsync(string? prompt)
    {
        if (_activeScheme is null || _runner is null) return;

        var enabled = _activeScheme.Groups.Where(g => g.Enabled).ToList();
        if (enabled.Count == 0)
        {
            StatusText.Text = "至少要勾选一个分组";
            return;
        }

        // 重新分配编号（只给勾选的）
        for (int i = 0; i < enabled.Count; i++) enabled[i].Index = i + 1;

        SetRunning(true, "准备分类…");
        ProgressArea.Visibility = Visibility.Visible;
        ResultArea.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();

        try
        {
            var results = await _runner.RunAsync(
                _activeScheme, _candidates, prompt,
                p => Dispatcher.Invoke(() =>
                {
                    PhaseText.Text = p.Phase;
                    StatsText.Text = p.StatsText;
                    var parentW = 300.0;
                    if (ProgressFill.Parent is Border pb && pb.ActualWidth > 10)
                        parentW = pb.ActualWidth;
                    ProgressFill.Width = Math.Max(0, parentW * p.Ratio);
                }),
                _cts.Token);

            _results = results;
            ResultItems.Clear();
            foreach (var r in results) ResultItems.Add(r);

            ResultArea.Visibility = Visibility.Visible;
            ResultCountText.Text = $"（{results.Count} 条，{results.Count(x => x.Selected)} 条勾选）";
            ApplyBtn.IsEnabled = results.Count > 0;
            ApplyBtn.Content = $"应用 {results.Count(x => x.Selected)} 条";

            var failNote = _runner.LastErrors.Count > 0
                ? $"（{_runner.LastErrors.Count} 批失败）"
                : "";
            StatusText.Text = $"分类完成：{results.Count} 条{failNote}";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消"; }
        catch (Exception ex) { StatusText.Text = "分类异常：" + ex.Message; }
        finally
        {
            SetRunning(false, null);
            ProgressArea.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ═══════ 结果操作 ═══════

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in ResultItems) r.Selected = true;
        ApplyBtn.Content = $"应用 {ResultItems.Count} 条";
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in ResultItems) r.Selected = false;
        ApplyBtn.Content = "应用 0 条";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultItems.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { StatusText.Text = "没有勾选任何条目"; return; }

        var ok = MessageBox.Show(this,
            $"将把 {selected.Count} 条移到对应分组，并打上标签。" +
            Environment.NewLine + "确定应用吗？",
            "应用整理", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        SetRunning(true, "正在写入…");

        try
        {
            var (done, message) = await _organize!.ApplyAsync(selected);

            // 记录到 organize_log
            await _organize.MarkDoneBatchAsync(selected);

            StatusText.Text = $"已应用 {done} 条：{message}";

            ApplyBtn.IsEnabled = false;
            ApplyBtn.Content = "已完成";

            App.NotifyGroupsChanged();
        }
        catch (Exception ex)
        {
            StatusText.Text = "应用失败：" + ex.Message;
        }
        finally
        {
            SetRunning(false, null);
        }
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…";
    }

    private void SetRunning(bool running, string? status)
    {
        _running = running;
        StartBtn.IsEnabled = !running;
        RunSchemeBtn.IsEnabled = !running;
        CancelRunBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (status is not null) StatusText.Text = status;
        if (running) ApplyBtn.IsEnabled = false;
    }
}

/// <summary>输入的预估条数显示。</summary>
public static class TaxonomyGroupExt
{
    public static string EstimateText(this TaxonomyGroup g)
        => g.Estimate > 0 ? $"{g.Estimate} 条" : "";
}
