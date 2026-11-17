using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DittoShell.Application.Services;
using DittoShell.Domain.Abstractions;

namespace DittoShell.App.ViewModels;

/// <summary>对话里的一条消息（给 UI 显示用）。</summary>
public sealed partial class ChatBubbleVM : ObservableObject
{
    public ChatBubbleVM(string role, string text)
    {
        Role = role;          // user / assistant / event
        Text = text;
    }

    [ObservableProperty] private string _role;
    [ObservableProperty] private string _text;

    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsEvent => Role == "event";

    public string Avatar => IsUser ? "你" : IsAssistant ? "AI" : "·";

    /// <summary>把占位气泡变成正式回答。</summary>
    public void RoleSwitchToAssistant()
    {
        Role = "assistant";
        OnPropertyChanged(nameof(IsUser));
        OnPropertyChanged(nameof(IsAssistant));
        OnPropertyChanged(nameof(IsEvent));
        OnPropertyChanged(nameof(Avatar));
    }

    /// <summary>事件行的图标（工具调用等）。</summary>
    [ObservableProperty] private string _eventIcon = "";

    [ObservableProperty] private bool _isRunning;
}

/// <summary>AI 抽屉的 ViewModel。</summary>
public sealed partial class AiChatViewModel : ObservableObject
{
    private readonly AiAgentService? _agent;
    private readonly DittoShell.Infrastructure.Sidecar.AiSessionStore? _store;
    private AgentSession? _session;
    private string _sessionIdForLog = "";

    public AiChatViewModel(AiAgentService? agent,
                           DittoShell.Infrastructure.Sidecar.AiSessionStore? store = null)
    {
        _agent = agent;
        _store = store;

        // 写操作弹窗确认
        var tools = App.ServiceProvider?.GetService(
            typeof(DittoShell.Application.Services.AiTools))
            as DittoShell.Application.Services.AiTools;

        if (tools is not null)
        {
            tools.OnConfirmWrite = async req =>
            {
                var ok = System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(
                        req.Describe() + Environment.NewLine + Environment.NewLine +
                        "这是 AI 主动发起的修改，确认执行吗？",
                        "AI 请求修改数据",
                        System.Windows.MessageBoxButton.OKCancel,
                        System.Windows.MessageBoxImage.Question)
                    == System.Windows.MessageBoxResult.OK);

                if (ok == true)
                {
                    Bubbles.Add(new ChatBubbleVM("event", "✓ " + req.Describe())
                        { EventIcon = "Check" });
                }
                else
                    Bubbles.Add(new ChatBubbleVM("event", "已取消：" + req.Describe())
                        { EventIcon = "Close" });

                return ok == true;
            };

            // 真正写完之后才刷新（不是确认时）
            tools.WriteCompleted += () =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(() => RequestRefresh?.Invoke()));
            };
        }
    }

    /// <summary>历史会话列表。</summary>
    public ObservableCollection<SessionItemVM> Sessions { get; } = new();
    [ObservableProperty] private bool _isSessionListOpen;

    [RelayCommand]
    private async Task ToggleSessionList()
    {
        IsSessionListOpen = !IsSessionListOpen;
        if (IsSessionListOpen) await RefreshSessionsAsync();
    }

    public async Task RefreshSessionsAsync()
    {
        Sessions.Clear();
        if (_store is null) return;

        var list = await _store.ListSessionsAsync(30);
        foreach (var (id, title, at, tok, cost) in list)
            Sessions.Add(new SessionItemVM(id, title, at, tok, cost));
    }

    public async Task OpenSessionAsync(SessionItemVM item)
    {
        if (_store is null) return;
        var loaded = await _store.LoadAsync(item.Id);
        if (loaded is null) return;

        _session = loaded;
        Bubbles.Clear();
        SessionTitle = loaded.Title.Length > 0 ? loaded.Title : "历史会话";

        // 重建气泡（跳过 system 摘要）
        foreach (var m in loaded.Messages)
        {
            if (m.Role == "system") continue;
            if (m.Role == "tool") continue;

            if (m.Role == "user")
                Bubbles.Add(new ChatBubbleVM("user", m.Content ?? ""));
            else if (m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))
                Bubbles.Add(new ChatBubbleVM("assistant", m.Content!));
        }

        // 恢复轨迹
        TraceItems.Clear();
        foreach (var e in loaded.Events)
        {
            if (e.Kind is "tool_start" or "tool_done" or "compressed" or "error")
                TraceItems.Add(new TraceItemVM(
                    e.Kind == "tool_done" ? "✓" : "→", e.Text, e.Round, DateTime.Now)
                { Detail = e.At });
        }

        UsageText = $"{loaded.TotalTokens:N0} tokens · {loaded.TotalCost:F4} 元";
        IsSessionListOpen = false;
        UpdateStats();
        StatusText = loaded.IsCompressed ? "（历史会话，已压缩过）" : "";
    }

    [RelayCommand]
    private async Task DeleteSession(SessionItemVM item)
    {
        if (_store is null) return;
        await _store.DeleteAsync(item.Id);
        Sessions.Remove(item);
    }

    public ObservableCollection<ChatBubbleVM> Bubbles { get; } = new();

    /// <summary>当前显示的标签页：chat / trace / stats。</summary>
    [ObservableProperty] private string _activeTab = "chat";

    public bool IsChatTab => ActiveTab == "chat";
    public bool IsTraceTab => ActiveTab == "trace";
    public bool IsStatsTab => ActiveTab == "stats";

    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatTab));
        OnPropertyChanged(nameof(IsTraceTab));
        OnPropertyChanged(nameof(IsStatsTab));
    }

    /// <summary>轨迹：每次工具调用一行。</summary>
    public ObservableCollection<TraceItemVM> TraceItems { get; } = new();

    /// <summary>用量统计。</summary>
    [ObservableProperty] private string _statsSummary = "还没有对话记录";

    public void UpdateStats()
    {
        if (_session is null || _session.Messages.Count == 0)
        {
            StatsSummary = "还没有对话记录";
            return;
        }

        var userMsgs = _session.Messages.Count(m => m.Role == "user");
        var toolCalls = TraceItems.Count;
        var compressed = _session.IsCompressed ? $"是（{_session.CompressCount} 次）" : "否";

        StatsSummary =
            $"消息数      {_session.Messages.Count}{Environment.NewLine}" +
            $"你的提问    {userMsgs}{Environment.NewLine}" +
            $"工具调用    {toolCalls}{Environment.NewLine}" +
            $"累计 tokens  {_session.TotalTokens:N0}{Environment.NewLine}" +
            $"累计费用    {_session.TotalCost:F4} 元{Environment.NewLine}" +
            $"上下文压缩  {compressed}{Environment.NewLine}" +
            $"会话标题    {_session.Title}";
    }

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isOpen;

    /// <summary>当前会话标题。</summary>
    [ObservableProperty] private string _sessionTitle = "新会话";

    /// <summary>累计 tokens / 成本。</summary>
    [ObservableProperty] private string _usageText = "";

    /// <summary>结果条目（AI 提到的），供主界面高亮。</summary>
    public List<long> LastResultIds { get; private set; } = new();

    public event Action<List<long>>? ResultIdsChanged;

    /// <summary>写操作完成后，请求主界面刷新（分组树、列表）。</summary>
    public event Action? RequestRefresh;

    [RelayCommand]
    private void NewSession()
    {
        _session = new AgentSession();
        _sessionIdForLog = _session.Id;
        Bubbles.Clear();
        SessionTitle = "新会话";
        UsageText = "";
        StatusText = "";
        InputText = "";
        LastResultIds = new List<long>();
        ResultIdsChanged?.Invoke(LastResultIds);

        Bubbles.Add(new ChatBubbleVM("assistant",
            "你好，我是剪贴板助手。可以问我：\n" +
            "· 上周的改枪码配置\n" +
            "· 找所有含密码的条目\n" +
            "· 最近复制了什么\n" +
            "· 有没有和 Ditto 相关的"));
    }

    [RelayCommand]
    private async Task Send()
    {
        if (IsBusy) return;

        var text = (InputText ?? "").Trim();
        if (text.Length == 0) return;

        if (_agent is null)
        {
            Bubbles.Add(new ChatBubbleVM("event", "AI 服务未初始化"));
            return;
        }

        _session ??= new AgentSession();

        // 只在本会话还没有任何消息时，去掉欢迎语
        if (_session.Messages.Count == 0 && Bubbles.Count > 0)
            Bubbles.Clear();

        InputText = "";
        IsBusy = true;
        StatusText = "";

        Bubbles.Add(new ChatBubbleVM("user", text));

        // 正在跑的指示
        var running = new ChatBubbleVM("event", "正在思考…") { IsRunning = true, EventIcon = "Loader" };
        Bubbles.Add(running);

        try
        {
            var result = await _agent.RunAsync(_session, text, ev =>
            {
                // 事件回调可能在后台线程，切回 UI
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyEvent(ev, running);
                }));
            });

            // 移除"正在跑"的占位
            running.IsRunning = false;
            running.Text = "";

            if (!result.Success)
            {
                Bubbles.Add(new ChatBubbleVM("event", "失败：" + (result.Error ?? "未知错误")));
            }
            else
            {
                // 用最终答案替换
                running.Text = result.Answer;
                running.RoleSwitchToAssistant();
            }

            SessionTitle = _session.Title.Length > 0 ? _session.Title : "新会话";
            UsageText = $"{result.TotalTokens:N0} tokens · {result.Cost:F4} 元 · {result.ElapsedMs / 1000.0:F1} 秒";

            LastResultIds = result.ResultIds;
            ResultIdsChanged?.Invoke(LastResultIds);

            StatusText = result.Compressed ? "（上下文已压缩）" : "";
            UpdateStats();

            // 持久化
            if (_store is null)
            {
                StatusText = "⚠ 会话存储未就绪，本次对话不会保存";
            }
            else if (_session is not null)
            {
                try
                {
                    await _store.SaveAsync(_session);
                    await RefreshSessionsAsync();
                }
                catch (Exception ex)
                {
                    StatusText = "⚠ 会话保存失败：" + ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            running.IsRunning = false;
            Bubbles.Add(new ChatBubbleVM("event", "异常：" + ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyEvent(AgentEvent ev, ChatBubbleVM running)
    {
        switch (ev.Kind)
        {
            case "thinking":
                running.Text = ev.Text;
                break;
            case "tool_start":
                Bubbles.Insert(Bubbles.IndexOf(running),
                    new ChatBubbleVM("event", ev.Text) { EventIcon = "Loader" });
                TraceItems.Add(new TraceItemVM("→", ev.Text, ev.Round, DateTime.Now));
                break;
            case "tool_done":
                Bubbles.Insert(Bubbles.IndexOf(running),
                    new ChatBubbleVM("event", ev.Text) { EventIcon = "Check" });
                if (TraceItems.Count > 0)
                {
                    var last = TraceItems[^1];
                    last.Status = "✓";
                    last.Detail = ev.Text;
                }
                break;
            case "compressed":
                Bubbles.Insert(Bubbles.IndexOf(running),
                    new ChatBubbleVM("event", ev.Text) { EventIcon = "Database" });
                break;
            case "error":
                running.Text = ev.Text;
                break;
            case "answer":
                running.Text = ev.Text;
                break;
        }
    }

    [RelayCommand]
    private void Clear() => NewSession();

    /// <summary>外部打开抽屉时调用。</summary>
    /// <summary>
    /// 首次打开抽屉时初始化。
    /// 关键：只看 _session 是否为 null，绝不因为 Bubbles 为空就重置会话
    /// （否则关一次抽屉再开，上下文就丢了）。
    /// </summary>
    public void EnsureWelcome()
    {
        if (_session is not null) return;

        NewSession();

        if (_store is null)
            Bubbles.Add(new ChatBubbleVM("event",
                "⚠ 会话存储未初始化（历史会话将不可用）。请重启软件试试。")
            { EventIcon = "Warning" });
    }
}

/// <summary>会话列表里的一项。</summary>
public sealed class SessionItemVM
{
    public SessionItemVM(string id, string title, DateTime updatedAt, int tokens, double cost)
    {
        Id = id;
        Title = title;
        UpdatedAt = updatedAt;
        Tokens = tokens;
        Cost = cost;
    }

    public string Id { get; }
    public string Title { get; }
    public DateTime UpdatedAt { get; }
    public int Tokens { get; }
    public double Cost { get; }

    public string TimeText => UpdatedAt.ToString("MM-dd HH:mm");
    public string MetaText => Tokens > 0 ? $"{Tokens:N0} tok" : "";
}

/// <summary>轨迹里的一条工具调用记录。</summary>
public sealed partial class TraceItemVM : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public TraceItemVM(string status, string text, int round, DateTime at)
    {
        Status = status;
        Text = text;
        Round = round;
        At = at;
    }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _status;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _detail = "";

    public string Text { get; }
    public int Round { get; }
    public DateTime At { get; }

    public string RoundText => $"第 {Round} 轮";
    public string TimeText => At.ToString("HH:mm:ss");
}
