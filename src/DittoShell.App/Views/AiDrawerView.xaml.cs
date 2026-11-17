using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DittoShell.App.ViewModels;

namespace DittoShell.App.Views;

/// <summary>AI 会话抽屉（参考 DSH 布局：左侧会话列表 + 右侧对话 + 底部输入/状态栏）。</summary>
public partial class AiDrawerView : UserControl
{
    private string _tab = "chat";

    public AiDrawerView()
    {
        InitializeComponent();
    }

    /// <summary>外部设置 DataContext（主窗口的 AiChatViewModel）。</summary>
    public void Attach(AiChatViewModel vm)
    {
        DataContext = vm;
        vm.EnsureWelcome();
    }

    public void FocusInput()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ChatInput?.Focus();
            ChatScroll?.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ScrollToEnd() => ChatScroll?.ScrollToEnd();

    /// <summary>会话列表列开关。</summary>
    private void ToggleSessionCol_Click(object sender, RoutedEventArgs e)
    {
        var open = SessionCol.Width.Value < 10;
        SessionCol.Width = open ? new GridLength(130) : new GridLength(0);

        if (open && DataContext is AiChatViewModel vm)
            _ = vm.RefreshSessionsAsync();
    }

    private async void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionItemVM item }
            && DataContext is AiChatViewModel vm)
        {
            await vm.OpenSessionAsync(item);
            ScrollToEnd();
            e.Handled = true;
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;

        _tab = tab;
        if (DataContext is AiChatViewModel vm)
        {
            vm.ActiveTab = tab;
            if (tab == "stats") vm.UpdateStats();
        }

        // 高亮当前标签
        foreach (var (btn, key) in new[] { (TabChat, "chat"), (TabTrace, "trace"), (TabStats, "stats") })
        {
            btn.Foreground = key == tab
                ? (System.Windows.Media.Brush)FindResource("B.Text2")
                : (System.Windows.Media.Brush)FindResource("B.Text3");
        }
    }

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (DataContext is AiChatViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>关闭抽屉（由主窗口处理）。</summary>
    public event Action? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
