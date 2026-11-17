using System.Windows;
using DittoShell.App.ViewModels;
using DittoShell.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DittoShell.App.Services;

/// <summary>
/// 单进程双窗口的调度中心。设置页与统计页已内嵌到主界面，不再单独开窗。
/// </summary>
public sealed class WindowManager : IDisposable
{
    private readonly IServiceProvider _sp;
    private QuickPanelView? _quick;
    private MainWindowView? _main;

    public WindowManager(IServiceProvider sp) => _sp = sp;

    public bool IsQuickPanelOpen => _quick?.IsVisible == true;

    // ───────── 快速面板 ─────────

    public void ToggleQuickPanel()
    {
        if (IsQuickPanelOpen) HideQuickPanel();
        else ShowQuickPanel();
    }

    public void ShowQuickPanel()
    {
        _quick ??= _sp.GetRequiredService<QuickPanelView>();

        if (_quick.DataContext is QuickPanelViewModel vm) vm.Reload();

        _quick.ShowNearCaret();
        _quick.Activate();
    }

    public void HideQuickPanel() => _quick?.Hide();

    // ───────── 主界面 ─────────

    public void ToggleMainWindow()
    {
        if (_main?.IsVisible == true) _main.Hide();
        else ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        HideQuickPanel();
        _main ??= _sp.GetRequiredService<MainWindowView>();
        _main.Show();
        _main.Activate();
    }

    /// <summary>快速面板 → 主界面（不新开窗口）。</summary>
    public void SwitchToMainFromQuick()
    {
        HideQuickPanel();
        ShowMainWindow();
    }

    // ───────── 内嵌页 ─────────

    /// <summary>打开主界面并切到设置页。</summary>
    public void ShowSettings()
    {
        ShowMainWindow();
        _main?.GoToSettings();
    }

    /// <summary>打开主界面并切到统计页。</summary>
    public void ShowStats()
    {
        ShowMainWindow();
        _main?.GoToStats();
    }

    public void HideAll()
    {
        _quick?.Hide();
        _main?.Hide();
    }

    public void Dispose()
    {
        _quick?.Close();
        _main?.Close();
    }
}
