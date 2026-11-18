using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;

namespace DittoShell.App.Views;

public partial class AiOrganizeWindow : Window
{
    private readonly IClipReader? _reader;
    private readonly AiService? _ai;
    private readonly OrganizeService? _organize;
    private readonly ITagStore? _tags;

    private readonly OrganizeSampler? _sampler;
    private readonly OrganizeAnalyzer? _analyzer;
    private readonly OrganizeRunner? _runner;
    private readonly OrganizePlanner? _planner;
    private readonly ConstraintParser? _parser;

    private OrganizeConstraints _constraints = new();
    private List<ClipItem> _candidates = new();
    private OrganizePreview? _preview;
    private AnalyzeResult? _analysis;
    private TaxonomyScheme? _activeScheme;
    private List<OrganizeItem> _results = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    public ObservableCollection<TaxonomyGroup> SchemeGroups { get; } = new();
    public ObservableCollection<TagVocabVM> VocabItems { get; } = new();
    public ObservableCollection<OrganizeItem> ResultItems { get; } = new();

    public AiOrganizeWindow()
    {
        InitializeComponent();

        _reader = App.ServiceProvider?.GetService(typeof(IClipReader)) as IClipReader;
        _ai = App.ServiceProvider?.GetService(typeof(AiService)) as AiService;
        _organize = App.ServiceProvider?.GetService(typeof(OrganizeService)) as OrganizeService;
        _tags = App.ServiceProvider?.GetService(typeof(ITagStore)) as ITagStore;

        if (_reader is not null && _ai is not null && _organize is not null)
        {
            _sampler = new OrganizeSampler(_reader);
            _analyzer = new OrganizeAnalyzer(_reader, _ai, _sampler, _organize);
            _runner = new OrganizeRunner(_reader, _ai, _organize);
            _planner = new OrganizePlanner(_reader, _tags, _organize);
            _parser = new ConstraintParser(_ai);
        }

        SchemeGroupList.ItemsSource = SchemeGroups;
        VocabList.ItemsSource = VocabItems;
        ResultList.ItemsSource = ResultItems;

        Loaded += async (_, _) =>
        {
            BuildConstraintsFromUi();
            await RefreshAsync();
        };
    }

    // ═══════ 初始化 ═══════

    private async Task RefreshAsync()
    {
        if (_planner is null) return;

        try
        {
            BuildConstraintsFromUi();

            var (cand, preview) = await _planner.BuildCandidatesAsync(GetMode(), _constraints);
            _candidates = cand;
            _preview = preview;

            // 更新三个模式的描述（带实际数字）
            var (incCand, incPrev) = await _planner.BuildCandidatesAsync("incremental", _constraints);
            var (fullCand, fullPrev) = await _planner.BuildCandidatesAsync("full", _constraints);
            var (retryCand, _) = await _planner.BuildCandidatesAsync("retry", _constraints);

            IncrementalDesc.Text = $"只处理新增的（{incCand.Count} 条）";
            FullDesc.Text = $"重新分类所有条目（{fullCand.Count} 条 · 约 {fullPrev.EstimatedMinutes:F1} 分钟 · 约 {fullPrev.EstimatedCost:F2} 元）";
            RetryDesc.Text = $"只重试之前失败的（{retryCand.Count} 条）";

            // 保护项显示实际数量
            KeepManualDesc.Text = preview.Skipped.TryGetValue("手动分类的", out var m)
                ? $"{m.Count} 条会被跳过" : "没有手动分类的条目";
            SkipSecretsText.Text = preview.Skipped.TryGetValue("凭据（你的设置）", out var s)
                ? $"跳过凭据（{s.Count} 条）" : "跳过凭据";
            SkipImagesText.Text = preview.Skipped.TryGetValue("图片（AI 看不懂）", out var i)
                ? $"跳过图片（{i.Count} 条）" : "跳过图片";
            SkipPrivateText.Text = preview.Skipped.TryGetValue("隐私（强制）", out var p)
                ? $"跳过隐私（{p.Count} 条，强制）" : "跳过隐私（强制）";

            UpdateStatus();
        }
        catch (Exception ex) { StatusText.Text = "读取失败：" + ex.Message; }
    }

    private string GetMode()
        => ModeFull.IsChecked == true ? "full"
         : ModeRetry.IsChecked == true ? "retry"
         : "incremental";

    private void BuildConstraintsFromUi()
    {
        _constraints.ChangeGroups = ChangeGroupsCheck.IsChecked == true;
        _constraints.ChangeTags = ChangeTagsCheck.IsChecked == true;

        _constraints.TagMode = TagModeOverwrite.IsChecked == true ? TagMode.Overwrite
            : TagModeOnlyEmpty.IsChecked == true ? TagMode.OnlyEmpty
            : TagModeAppend.IsChecked == true ? TagMode.Append
            : TagMode.Skip;

        _constraints.KeepManuallyGrouped = KeepManualCheck.IsChecked == true;
        _constraints.SkipSecrets = SkipSecretsCheck.IsChecked == true;
        _constraints.SkipImages = SkipImagesCheck.IsChecked == true;
    }

    private void UpdateStatus()
    {
        if (_preview is null) return;

        var skip = _preview.Skipped.Count == 0
            ? "无跳过"
            : "跳过 " + string.Join(" + ", _preview.Skipped.Select(kv => $"{kv.Key} {kv.Value.Count}"));

        StatusText.Text = $"将处理 {_preview.WillProcess:N0} 条 · {skip} · " +
                          $"约 {_preview.EstimatedMinutes:F1} 分钟 · 约 {_preview.EstimatedCost:F2} 元";
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

    private async void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private void ChangeTags_Changed(object sender, RoutedEventArgs e)
    {
        if (TagModePanel is null) return;
        TagModePanel.IsEnabled = ChangeTagsCheck.IsChecked == true;
    }

    private async void Protect_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private void AnalyzeFirst_Changed(object sender, RoutedEventArgs e) { }

    // ═══════ 约束解析 ═══════

    private async void Parse_Click(object sender, RoutedEventArgs e)
    {
        if (_parser is null) { StatusText.Text = "解析器未就绪"; return; }

        var text = PromptBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "请先输入你的要求";
            return;
        }

        ParseBtn.IsEnabled = false;
        StatusText.Text = "正在解析约束…";

        try
        {
            BuildConstraintsFromUi();
            var parsed = await _parser.ParseAsync(text, _constraints);
            _constraints = parsed;

            // 应用到 UI
            ChangeGroupsCheck.IsChecked = parsed.ChangeGroups;
            ChangeTagsCheck.IsChecked = parsed.ChangeTags;
            TagModeOverwrite.IsChecked = parsed.TagMode == TagMode.Overwrite;
            TagModeOnlyEmpty.IsChecked = parsed.TagMode == TagMode.OnlyEmpty;
            TagModeAppend.IsChecked = parsed.TagMode == TagMode.Append;
            TagModeSkip.IsChecked = parsed.TagMode == TagMode.Skip;
            KeepManualCheck.IsChecked = parsed.KeepManuallyGrouped;
            SkipSecretsCheck.IsChecked = parsed.SkipSecrets;
            SkipImagesCheck.IsChecked = parsed.SkipImages;
            TagModePanel.IsEnabled = parsed.ChangeTags;

            // 显示解析结果
            ParseResultBox.Visibility = Visibility.Visible;
            ParseTitleText.Text = parsed.ParseOk ? "✓ AI 理解的约束（已应用到上方开关）"
                                                 : "⚠ 解析失败（已保留开关设置）";

            var lines = new List<string>();
            if (parsed.ChangeGroups) lines.Add("• 改分组");
            if (parsed.ChangeTags)
            {
                lines.Add("• 改标签：" + parsed.TagMode switch
                {
                    TagMode.Overwrite => "覆盖",
                    TagMode.OnlyEmpty => "只补空白",
                    TagMode.Append => "追加",
                    _ => "不动"
                });
            }
            if (parsed.KeepManuallyGrouped) lines.Add("• 保留手动分类");
            if (parsed.SkipSecrets) lines.Add("• 跳过凭据");
            if (parsed.SkipImages) lines.Add("• 跳过图片");
            lines.Add($"• 标签上限 {parsed.MaxTags} 个");
            foreach (var r in parsed.ExtraRules) lines.Add("• " + r);
            foreach (var n in parsed.ParseNotes) lines.Add("ℹ " + n);

            ParseDetailText.Text = string.Join("\n", lines);

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "解析失败：" + ex.Message;
        }
        finally { ParseBtn.IsEnabled = true; }
    }

    // ═══════ 分析 ═══════

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_analyzer is null || _runner is null) { StatusText.Text = "服务未初始化"; return; }
        if (_ai?.Enabled != true) { StatusText.Text = "AI 没启用（设置 → AI）"; return; }

        var analyzeFirst = AnalyzeFirstCheck.IsChecked == true;
        var prompt = PromptBox?.Text?.Trim();

        if (analyzeFirst)
            await RunAnalyzeAsync(prompt);
        else
        {
            _activeScheme = new TaxonomyScheme
            {
                Name = "自动分类",
                Groups = new List<TaxonomyGroup>
                {
                    new() { Index = 1, Path = "00 待清理", Reason = "无效/测试内容" }
                }
            };
            await RunClassifyAsync(prompt);
        }
    }

    private async Task RunAnalyzeAsync(string? prompt)
    {
        SetRunning(true, "正在分析…");
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

            // 标签词表
            VocabItems.Clear();
            foreach (var v in result.TagVocabulary.Take(result.TopTagsLimit()))
                VocabItems.Add(new TagVocabVM(v.Name, v.Expected));
            VocabCountText.Text = $"{VocabItems.Count} 个（取消勾选可移除）";

            SchemeArea.Visibility = Visibility.Visible;
            StartBtn.Visibility = Visibility.Collapsed;   // 分析完了，底部按钮换成方案里的
            StatusText.Text = $"分析完成：{result.Schemes.Count} 套方案 · " +
                              $"{result.TagVocabulary.Count} 个标签 · " +
                              $"{(result.Tokens / 1000.0):F1}k tokens（约 {result.Cost:F3} 元）";
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
                Height = 28,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 8, 0),
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
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaxonomyGroup g }) return;

        var dlg = new InputDialog("改分组名", g.Path) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            g.Path = dlg.Value.Trim();
            SchemeGroups.Remove(g);
            SchemeGroups.Insert(0, g);
        }
    }

    // ═══════ 预览 ═══════

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) { StatusText.Text = "先选一个整理范围"; return; }

        var lines = new List<string>
        {
            $"总条目：{_preview.TotalCandidates:N0} 条",
            $"将处理：{_preview.WillProcess:N0} 条",
        };

        if (_preview.Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add("跳过明细：");
            foreach (var (reason, ids) in _preview.Skipped)
            {
                lines.Add($"  {reason}：{ids.Count} 条");
                if (_preview.Samples.TryGetValue(reason, out var samples))
                    foreach (var s in samples)
                        lines.Add($"    {s}");
                if (ids.Count > (_preview.Samples.TryGetValue(reason, out var sm) ? sm.Count : 0))
                    lines.Add($"    …还有 {ids.Count - 5} 条（点「导出明细」看全部）");
            }
        }

        var dlg = new PreviewDialog(lines, _preview.Skipped) { Owner = this };
        dlg.ShowDialog();
    }

    private async void RunScheme_Click(object sender, RoutedEventArgs e)
    {
        if (_activeScheme is null) { StatusText.Text = "先分析生成方案"; return; }
        await RunClassifyAsync(PromptBox?.Text?.Trim());
    }

    // ═══════ 分类（先算完再写）═══════

    private async Task RunClassifyAsync(string? prompt)
    {
        if (_activeScheme is null || _runner is null || _planner is null) return;

        var enabled = _activeScheme.Groups.Where(g => g.Enabled).ToList();
        if (enabled.Count == 0) { StatusText.Text = "至少要勾选一个分组"; return; }

        // 方案里的编号重新排
        for (int i = 0; i < enabled.Count; i++) enabled[i].Index = i + 1;

        // 标签词表（只取勾选的）
        var vocab = VocabItems.Where(v => v.Enabled)
            .Select(v => new TagVocabItem { Name = v.Name, Expected = v.Expected })
            .ToList();

        // 应用标签上限
        if (_constraints.TagMode != TagMode.Skip && _constraints.ChangeTags)
            vocab = vocab.Take(_constraints.MaxTags).ToList();

        // 重新筛候选（约束可能变了）
        var (cand, preview) = await _planner.BuildCandidatesAsync(GetMode(), _constraints);
        _candidates = cand;
        _preview = preview;

        if (cand.Count == 0) { StatusText.Text = "没有需要处理的条目"; return; }

        // 确认
        var confirm = MessageBox.Show(this,
            $"将处理 {cand.Count:N0} 条" +
            (preview.Skipped.Count > 0
                ? $"，跳过 {string.Join(" + ", preview.Skipped.Select(kv => $"{kv.Key} {kv.Value.Count}"))}"
                : "") +
            Environment.NewLine + Environment.NewLine +
            $"约 {preview.EstimatedMinutes:F1} 分钟 · 约 {preview.EstimatedCost:F2} 元" +
            Environment.NewLine + Environment.NewLine +
            "所有结果算完后才会写入。中途取消不会改动任何数据。",
            "开始整理", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        SetRunning(true, "准备分类…");
        ProgressArea.Visibility = Visibility.Visible;
        ResultArea.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();

        try
        {
            // 用 planner 的 system prompt（含词表 + 约束）
            var results = await _runner.RunWithConstraintsAsync(
                _activeScheme, cand, vocab, _constraints, prompt,
                p => Dispatcher.Invoke(() =>
                {
                    PhaseText.Text = p.Phase;
                    StatsText.Text = p.StatsText;
                    if (ProgressFill.Parent is Border pb && pb.ActualWidth > 10)
                        ProgressFill.Width = Math.Max(0, pb.ActualWidth * p.Ratio);
                }),
                _cts.Token);

            _results = results;
            ResultItems.Clear();
            foreach (var r in results) ResultItems.Add(r);

            ResultArea.Visibility = Visibility.Visible;
            ResultCountText.Text = $"（{results.Count} 条）";
            StartBtn.Visibility = Visibility.Collapsed;
            ApplyBtn.Visibility = Visibility.Visible;
            ApplyBtn.IsEnabled = results.Count > 0;
            ApplyBtn.Content = $"应用 {results.Count} 条";

            StatusText.Text = $"分类完成：{results.Count} 条（未写入，点「应用」才生效）";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消（未写入任何数据）"; }
        catch (Exception ex) { StatusText.Text = "分类异常：" + ex.Message; }
        finally
        {
            SetRunning(false, null);
            ProgressArea.Visibility = Visibility.Collapsed;

            // 兜底：结果区显示了就一定要能点
            if (ResultArea.Visibility == Visibility.Visible && _results.Count > 0)
            {
                ApplyBtn.Visibility = Visibility.Visible;
                ApplyBtn.IsEnabled = ResultItems.Any(x => x.Selected);
                ApplyBtn.Content = $"应用 {ResultItems.Count(x => x.Selected)} 条";
            }

            _cts?.Dispose();
            _cts = null;
        }
    }

    // ═══════ 结果操作 ═══════

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in ResultItems) r.Selected = true;
        ApplyBtn.Content = $"应用 {ResultItems.Count} 条";
        ApplyBtn.IsEnabled = ResultItems.Count > 0;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in ResultItems) r.Selected = false;
        ApplyBtn.Content = "应用 0 条";
        ApplyBtn.IsEnabled = false;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultItems.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { StatusText.Text = "没有勾选任何条目"; return; }

        var ok = MessageBox.Show(this,
            $"将把 {selected.Count} 条写入 Ditto。" + Environment.NewLine + Environment.NewLine +
            "写入前会自动备份数据库。",
            "应用整理", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        // 显示进度区
        SetRunning(true, "正在写入…");
        ProgressArea.Visibility = Visibility.Visible;
        PhaseText.Text = "准备写入…";
        StatsText.Text = $"共 {selected.Count} 条";
        ProgressFill.Width = 0;

        // 按钮文案：写死「写入中」防止重复点击
        ApplyBtn.IsEnabled = false;
        ApplyBtn.Content = "写入中…";

        try
        {
            var (success, done, msg) = await _planner!.ApplyAllAsync(
                selected,
                (cur, total, phase) => Dispatcher.Invoke(() =>
                {
                    PhaseText.Text = phase;
                    StatsText.Text = $"{cur}/{total}";
                    if (ProgressFill.Parent is Border pb && pb.ActualWidth > 10)
                        ProgressFill.Width = Math.Max(0, pb.ActualWidth * (total > 0 ? (double)cur / total : 0));
                }));

            StatusText.Text = success ? $"✓ 已应用 {done} 条：{msg}" : "✗ " + msg;

            if (success)
            {
                ApplyBtn.Content = $"✓ 已完成（{done} 条）";
                ResultCountText.Text = $"（已应用 {done} 条）";
                App.NotifyGroupsChanged();
            }
            else
            {
                ApplyBtn.IsEnabled = true;
                ApplyBtn.Content = "重试应用";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "✗ 写入失败：" + ex.Message;
            ApplyBtn.IsEnabled = true;
            ApplyBtn.Content = "重试应用";
        }
        finally
        {
            SetRunning(false, null);
            PhaseText.Text = "完成";
            ProgressArea.Visibility = Visibility.Collapsed;

            // 失败时允许重试
            if (ApplyBtn.Content?.ToString()?.StartsWith("✗") != true &&
                ApplyBtn.Content?.ToString()?.StartsWith("✓") != true)
            {
                ApplyBtn.IsEnabled = ResultItems.Any(x => x.Selected);
            }
        }
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…（已算的结果不会写入）";
    }

    private void SetRunning(bool running, string? status)
    {
        _running = running;
        StartBtn.IsEnabled = !running;
        if (RunSchemeBtn is not null) RunSchemeBtn.IsEnabled = !running;
        PreviewBtn.IsEnabled = !running;
        ParseBtn.IsEnabled = !running;
        CancelRunBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (status is not null) StatusText.Text = status;

        // 应用按钮：运行时禁用；跑完且有条目就启用
        if (running)
        {
            ApplyBtn.IsEnabled = false;
        }
        else
        {
            var hasResult = _results.Count > 0;
            var hasSelected = ResultItems.Any(x => x.Selected);
            ApplyBtn.IsEnabled = hasResult && hasSelected;
        }
    }
}

/// <summary>标签词表里的一项。</summary>
public sealed partial class TagVocabVM : System.ComponentModel.INotifyPropertyChanged
{
    public TagVocabVM(string name, int expected)
    {
        Name = name;
        Expected = expected;
        _enabled = true;
    }

    public string Name { get; }
    public int Expected { get; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Raise(nameof(Enabled)); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this,
        new System.ComponentModel.PropertyChangedEventArgs(n));
}

/// <summary>预览对话框（统计 + 样例 + 导出）。</summary>
public sealed class PreviewDialog : Window
{
    public PreviewDialog(List<string> lines, Dictionary<string, List<long>> skipped)
    {
        Title = "整理预览";
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "整理预览",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var tb = new TextBox
        {
            Text = string.Join("\n", lines),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        scroll.Content = tb;
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var exportBtn = new Button
        {
            Content = "导出完整明细",
            Width = 120,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = skipped.Count > 0
        };
        exportBtn.Click += (_, _) =>
        {
            var save = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"整理明细_{DateTime.Now:yyyyMMdd_HHmm}.txt",
                Filter = "文本文件|*.txt"
            };
            if (save.ShowDialog() != true) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# 整理预览明细 · {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            foreach (var (reason, ids) in skipped)
            {
                sb.AppendLine($"## {reason}（{ids.Count} 条）");
                foreach (var id in ids) sb.AppendLine($"#{id}");
                sb.AppendLine();
            }
            System.IO.File.WriteAllText(save.FileName, sb.ToString(),
                new System.Text.UTF8Encoding(true));
        };

        var closeBtn = new Button { Content = "关闭", Width = 90, Height = 30, IsCancel = true };

        panel.Children.Add(exportBtn);
        panel.Children.Add(closeBtn);
        Grid.SetRow(panel, 2);
        grid.Children.Add(panel);

        Content = grid;
    }
}

/// <summary>AnalyzeResult 的辅助扩展。</summary>
public static class AnalyzeExt
{
    public static int TopTagsLimit(this AnalyzeResult r)
        => Math.Clamp(r.TagVocabulary.Count, 20, 60);
}
