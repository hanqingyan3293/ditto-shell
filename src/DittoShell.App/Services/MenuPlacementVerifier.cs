using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace DittoShell.App.Services;

/// <summary>独立菜单回归；不连接数据库、不读写剪贴板、不执行产品命令。</summary>
internal static class MenuPlacementVerifier
{
    private sealed record Result(string Name, bool Passed, string Detail);
    private static ContextMenu? _lastOpened;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    public static async Task<int> RunAsync(string reportPath)
    {
        var results = new List<Result>();
        void Check(string name, bool passed, string detail) => results.Add(new(name, passed, detail));
        if (!SystemParametersInfo(0x001B, 0, out var nativeAlignment, 0))
            throw new InvalidOperationException("SPI_GETMENUDROPALIGNMENT failed.");
        GetCursorPos(out var originalCursor);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => _lastOpened = (ContextMenu)sender));
        var wpfVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(SystemParameters).Assembly.Location).ProductVersion;
        Check("process-alignment", !SystemParameters.MenuDropAlignment,
            $"native={nativeAlignment}; WPF={SystemParameters.MenuDropAlignment}; framework={wpfVersion}");

        try
        {
            // 调用已核实的 WPF 缓存失效路径，不调用 Windows 的设置接口，
            // 也不手动重跑修复；这里验证系统通知处理器会同步保持右开。
            try
            {
                var invalidate = typeof(SystemParameters).GetMethod("InvalidateCache",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    binder: null, types: new[] { typeof(int) }, modifiers: null)
                    ?? throw new MissingMethodException("SystemParameters.InvalidateCache(int)");
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    _ = SystemParameters.MenuDropAlignment;
                    var invalidated = invalidate.Invoke(null, new object[] { 0x001C });
                    Check($"cache-refresh-{attempt}", !SystemParameters.MenuDropAlignment,
                        $"invalidated={invalidated}; effective={SystemParameters.MenuDropAlignment}; native={nativeAlignment}");
                }
            }
            catch (Exception ex) { Check("cache-refresh-exception", false, ex.ToString()); }

            foreach (var screen in WinForms.Screen.AllScreens)
            {
                foreach (var kind in new[] { "normal", "chrome", "transparent" })
                {
                    var window = CreateWindow(kind, out var anchor, out var editors);
                    try
                    {
                        window.Show();
                        // 移动测试宿主到指定显示器，菜单本身完全由 WPF 定位。
                        var area = screen.WorkingArea;
                        var handle = new WindowInteropHelper(window).Handle;
                        if (!SetWindowPos(handle, IntPtr.Zero, area.Left + area.Width / 3, area.Top + 70, 0, 0, 0x0015))
                            throw new InvalidOperationException("Cannot position verification window.");
                        window.Activate();
                        await SettleAsync();
                        window.UpdateLayout();
                        var name = $"{screen.DeviceName}/{kind}";
                        for (var attempt = 1; attempt <= 2; attempt++)
                            await VerifyHierarchyAsync(anchor, screen, $"{name}/open-{attempt}", false, Check);
                        if (kind == "chrome")
                        {
                            foreach (var editor in editors)
                                await VerifyEditorAsync(editor, $"{name}/editor-{editor.Tag}", Check);
                            await VerifyHierarchyAsync(anchor, screen, $"{name}/right-edge", true, Check);
                        }
                    }
                    catch (Exception ex) { Check($"{screen.DeviceName}/{kind}/exception", false, ex.ToString()); }
                    finally
                    {
                        if (_lastOpened is not null) _lastOpened.IsOpen = false;
                        window.Close();
                    }
                }
            }
        }
        finally { SetCursorPos(originalCursor.X, originalCursor.Y); }

        var nativeRead = SystemParametersInfo(0x001B, 0, out var nativeAfter, 0);
        Check("system-setting-unchanged", nativeRead && nativeAfter == nativeAlignment,
            $"before={nativeAlignment}; after={nativeAfter}");
        var passed = results.Count(x => x.Passed);
        var failed = results.Count - passed;
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new
        {
            Passed = passed, Failed = failed, NativeAlignment = nativeAlignment, Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static Window CreateWindow(string kind, out Border anchor, out TextBox[] editors)
    {
        var panel = new StackPanel { Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Left };
        anchor = new Border
        {
            Width = 300, Height = 60, Background = Brushes.LightGray,
            Child = new TextBlock { Text = "菜单定位验证", Margin = new Thickness(12) }
        };
        panel.Children.Add(anchor);
        editors = new[]
        {
            new TextBox { Tag = "settings", Style = (Style)System.Windows.Application.Current.FindResource("AntTextBox") },
            new TextBox { Tag = "search", Style = (Style)System.Windows.Application.Current.FindResource("BorderlessTextBox") },
            new TextBox { Tag = "detail", IsReadOnly = false, BorderThickness = new Thickness(0),
                Style = new Style(typeof(TextBox)) }
        };
        foreach (var editor in editors)
        {
            editor.Text = "Menu verification text";
            editor.Width = 300;
            editor.Height = 38;
            editor.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(editor);
        }
        var window = new Window
        {
            Title = "DittoShell menu verification", Width = 480, Height = 330,
            Content = panel, Background = Brushes.White, ShowInTaskbar = false
        };
        if (kind != "normal") window.WindowStyle = WindowStyle.None;
        if (kind == "chrome")
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(8),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0)
            });
        if (kind == "transparent") window.AllowsTransparency = true;
        return window;
    }

    private static async Task VerifyHierarchyAsync(Border anchor, WinForms.Screen screen, string name,
        bool atEdge, Action<string, bool, string> check)
    {
        var clicked = false;
        var leaf = new MenuItem { Header = "验证点击" };
        leaf.Click += (_, _) => clicked = true;
        var nested = new MenuItem { Header = "更多" };
        nested.Items.Add(leaf);
        var group = new MenuItem { Header = "组" };
        group.Items.Add(nested);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = anchor };
        menu.Items.Add(group);
        anchor.ContextMenu = menu;
        var point = anchor.PointToScreen(new Point(20, 20));
        var x = atEdge ? screen.WorkingArea.Right - 3 : (int)point.X;
        var y = atEdge ? screen.WorkingArea.Top + 300 : (int)point.Y;
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Cannot position verification cursor.");
        try
        {
            menu.IsOpen = true;
            await SettleAsync();
            var first = PopupBounds(menu);
            check(name + "/primary", atEdge
                    ? first.Left < x - 30 && Inside(first, screen)
                    : Math.Abs(first.Left - x) <= 24 && first.Right > x + 30,
                $"cursor=({x},{y}); popup={first}; edge={atEdge}");

            group.Focus();
            SendKey(group, Key.Right, Keyboard.KeyDownEvent);
            await SettleAsync();
            check(name + "/keyboard-open", group.IsSubmenuOpen, "Right arrow opens the submenu.");
            if (!group.IsSubmenuOpen) group.IsSubmenuOpen = true;
            await SettleAsync();
            var second = PopupBounds(nested);
            nested.IsSubmenuOpen = true;
            await SettleAsync();
            var third = PopupBounds(leaf);
            check(name + "/secondary", atEdge ? Inside(second, screen) : OnRight(second, first),
                $"parent={first}; popup={second}");
            check(name + "/tertiary", atEdge ? Inside(third, screen) : OnRight(third, second),
                $"parent={second}; popup={third}");
            check(name + "/work-area", Inside(first, screen) && Inside(second, screen) && Inside(third, screen),
                $"working-area={screen.WorkingArea}; primary={first}; secondary={second}; tertiary={third}");

            ((IInvokeProvider)new MenuItemAutomationPeer(leaf).GetPattern(PatternInterface.Invoke)).Invoke();
            await SettleAsync();
            check(name + "/click", clicked, "The actual MenuItem Click handler ran.");
        }
        finally
        {
            menu.IsOpen = false;
            anchor.ContextMenu = null;
        }
    }

    private static async Task VerifyEditorAsync(TextBox editor, string name, Action<string, bool, string> check)
    {
        _lastOpened = null;
        editor.Focus();
        editor.Select(0, 1);
        await SettleAsync();
        var caret = editor.GetRectFromCharacterIndex(0);
        var expected = editor.PointToScreen(caret.BottomLeft);
        SendKey(editor, Key.Apps, Keyboard.KeyUpEvent);
        await SettleAsync();
        var menu = _lastOpened;
        check(name + "/default-menu", editor.ContextMenu is null && menu?.IsOpen == true,
            $"local-property={editor.ReadLocalValue(FrameworkElement.ContextMenuProperty)}; temporary-menu-open={menu?.IsOpen}");
        if (menu is null || !menu.IsOpen) return;
        try
        {
            var rect = PopupBounds(menu);
            check(name + "/direction", Math.Abs(rect.Left - expected.X) <= 30,
                $"caret=({expected.X},{expected.Y}); popup={rect}; placement={menu.Placement}");
            var copy = menu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Command == ApplicationCommands.Copy);
            var clicked = false;
            var routed = false;
            // 只在一次性测试编辑框中拦截命令；验证真实菜单点击与命令路由，绝不写剪贴板。
            ExecutedRoutedEventHandler intercept = (_, e) =>
            {
                if (e.Command != ApplicationCommands.Copy) return;
                routed = true;
                e.Handled = true;
            };
            editor.AddHandler(CommandManager.PreviewExecutedEvent, intercept);
            try
            {
                if (copy is not null)
                {
                    copy.Click += (_, _) => clicked = true;
                    ((IInvokeProvider)new MenuItemAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                await SettleAsync();
                check(name + "/command", copy is not null && clicked && routed,
                    $"Copy item found={copy is not null}; click={clicked}; command-routed={routed}; clipboard-execution-suppressed=True");
            }
            finally { editor.RemoveHandler(CommandManager.PreviewExecutedEvent, intercept); }
        }
        finally { menu.IsOpen = false; }
    }

    private static void SendKey(Visual target, Key key, RoutedEvent routedEvent)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("No input source.");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        { RoutedEvent = routedEvent });
    }

    private static NativeRect PopupBounds(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual) as HwndSource;
        if (source is null || !GetWindowRect(source.Handle, out var rect))
            throw new InvalidOperationException("The popup has no native window.");
        return rect;
    }

    private static bool OnRight(NativeRect child, NativeRect parent) =>
        child.Left >= parent.Right - 24 && child.Left > (parent.Left + parent.Right) / 2;

    private static bool Inside(NativeRect rect, WinForms.Screen screen)
    {
        var area = screen.WorkingArea;
        return rect.Left >= area.Left - 2 && rect.Top >= area.Top - 2 &&
            rect.Right <= area.Right + 2 && rect.Bottom <= area.Bottom + 2;
    }

    private static async Task SettleAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(160);
    }
}
