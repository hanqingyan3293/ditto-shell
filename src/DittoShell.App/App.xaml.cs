using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DittoShell.App.Localization;
using DittoShell.App.Services;
using DittoShell.App.ViewModels;
using DittoShell.App.Views;
using DittoShell.Domain.Abstractions;
using DittoShell.Domain.Models;
using DittoShell.App.Media;
using DittoShell.Infrastructure.Data;
using DittoShell.Infrastructure.DittoDb;
using DittoShell.Infrastructure.Ipc;
using DittoShell.Infrastructure.Settings;
using DittoShell.Infrastructure.Sidecar;
using DittoShell.Infrastructure.Configuration;
using DittoShell.Infrastructure.Stubs;
using Microsoft.Extensions.DependencyInjection;
using WinForms = System.Windows.Forms;

namespace DittoShell.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static AppConfig Config { get; private set; } = new();

    private static DittoShell.Infrastructure.Configuration.ConfigStore? _configStore;

    /// <summary>修改 Config 后调用，写回 data/config.json。</summary>
    public static void SaveConfig() => _configStore?.Save(Config);

    /// <summary>列表密度变化时通知主窗口。</summary>
    public static event Action<int>? DensityChanged;
    public static void NotifyDensityChanged(int value) => DensityChanged?.Invoke(value);

    /// <summary>分组/标签变化时通知主界面重载。</summary>
    public static event Action? GroupsChanged;
    public static void NotifyGroupsChanged() => GroupsChanged?.Invoke();

    /// <summary>侧栏配置变化（设置页改了顺序或显隐）。</summary>
    public static event Action? SidebarChanged;
    public static void NotifySidebarChanged() => SidebarChanged?.Invoke();
    public static bool IsShuttingDown { get; private set; }

    /// <summary>截图/无人值守模式：禁止任何写盘动作。</summary>
    public static bool IsShotMode { get; private set; }

    /// <summary>全局热键服务（设置页改键时需要）。</summary>
    public static HotkeyService? HotkeySvc { get; private set; }

    /// <summary>探测到的 Ditto 数据库路径（可能为 null）。</summary>
    public static string? DetectedDatabase { get; private set; }

    /// <summary>数据库是否可用；false 时界面显示的是占位数据。</summary>
    public static bool DatabaseAvailable { get; private set; }

    public WindowManager? WindowsManager { get; private set; }
    public HotkeyService? Hotkeys { get; private set; }

    private WinForms.NotifyIcon? _tray;
    private string? _pendingQuery;

    // 菜单验证使用的原生坐标读取；菜单定位本身交给 WPF。

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosGlobal(out GPoint lpPoint);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GPoint { public int X; public int Y; }

    // menuverify 测试用
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool SetCursorPos(int X, int Y);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(IntPtr hWnd, out GRect lpRect);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GRect { public int Left, Top, Right, Bottom; }


    private static void LogMenu(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "menu.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>保留各页面的打开通知，不再从外部移动 Popup 窗口。</summary>
    public static void MenuOpened(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        var owner = Window.GetWindow(menu.PlacementTarget ?? menu)?.GetType().Name ?? "no-window";
        LogMenu($"[{owner}] Opened placement={menu.Placement} rightAligned={SystemParameters.MenuDropAlignment}");
    }

    /// <summary>
    /// .NET 8 WPF 没有公开的进程级菜单方向开关。适配它的已核实缓存，
    /// 只改变本外壳的默认展开方向，保留原生键盘导航、点击及屏幕避让。
    /// 升级 WPF 时须运行 --verify-menus 检查此私有字段兼容性。
    /// </summary>
    private static void InitializeMenuAlignment()
    {
        var cache = typeof(SystemParameters).GetField("_menuDropAlignment",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (cache is null || cache.FieldType != typeof(bool) || cache.IsInitOnly)
        {
            LogMenu("[alignment] ERROR unsupported WPF menu alignment cache; keeping native behavior");
            return;
        }

        void Apply()
        {
            try
            {
                // 先完成 getter 的缓存初始化，否则下一次读取会恢复系统值。
                var nativeAlignment = SystemParameters.MenuDropAlignment;
                cache.SetValue(null, false);
                LogMenu($"[alignment] cached={nativeAlignment} effective={SystemParameters.MenuDropAlignment}");
            }
            catch (Exception ex) { LogMenu("[alignment] ERROR " + ex.Message); }
        }

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.MenuDropAlignment))
                Apply();
        };
        Apply();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var verifyIndex = Array.IndexOf(e.Args, "--verify-menus");
        if (verifyIndex < 0 || !e.Args.Contains("--native-menu-alignment")) InitializeMenuAlignment();
        if (verifyIndex >= 0)
        {
            // 独立 UI 回归入口：不初始化配置、旁路库、Ditto 通信或全局热键。
            IsShotMode = true;
            base.OnStartup(e);
            var report = verifyIndex + 1 < e.Args.Length && !e.Args[verifyIndex + 1].StartsWith("--")
                ? e.Args[verifyIndex + 1]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "menu-verification.json");
            try { Shutdown(await MenuPlacementVerifier.RunAsync(report)); }
            catch (Exception ex)
            {
                LogMenu("[verification] ERROR " + ex);
                Console.Error.WriteLine(ex);
                Shutdown(1);
            }
            return;
        }

        _mutex = new Mutex(true, "DittoShell.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Ditto Shell 已在运行。", "Ditto Shell");
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _configStore = new DittoShell.Infrastructure.Configuration.ConfigStore();
        Config = _configStore.Load();

        L.Instance.Load(Config.Language);
        ThemeManager.Apply(Config.Theme);

        // ───────── 定位 Ditto ─────────
        var located = DittoLocator.Detect(Config.DittoPath);
        DetectedDatabase = located?.DatabasePath;
        DatabaseAvailable = located is not null && DittoLocator.LooksLikeDittoDb(located.DatabasePath, out _);

        // ───────── DI ─────────
        var sc = new ServiceCollection();
        sc.AddSingleton<ThumbnailCache>();

        if (DatabaseAvailable && located is not null)
        {
            var backup = new BackupService();
            var bridge = new DittoBridge(located.DittoExe);

            sc.AddSingleton(backup);
            sc.AddSingleton(bridge);
            // 同时注册具体类型和接口（具体类型用于 InvalidateKindCounts 等）
            sc.AddSingleton<DittoDbReader>(sp =>
            {
                var reader = new DittoDbReader(located.DatabasePath);

                // 让搜索能用 tag: / from: 条件（要在 sidecar 建表之后注入）
                var tags = sp.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                           as DittoShell.Domain.Abstractions.ITagStore;
                var srcs = sp.GetService(typeof(DittoShell.Domain.Abstractions.ISourceStore))
                           as DittoShell.Domain.Abstractions.ISourceStore;

                if (tags is not null && srcs is not null) reader.AttachFilters(tags, srcs);
                return reader;
            });
            sc.AddSingleton<IClipReader>(sp => sp.GetRequiredService<DittoDbReader>());
            var writer = new DittoDbWriter(located.DatabasePath, backup, bridge);
            sc.AddSingleton(writer);
            sc.AddSingleton<IClipWriter>(writer);
            sc.AddSingleton<IDittoBridge>(bridge);
            sc.AddSingleton<DittoShell.Application.Services.GroupService>();
            sc.AddSingleton<DittoShell.Application.Services.GroupOpsService>(sp =>
                new DittoShell.Application.Services.GroupOpsService(
                    sp.GetRequiredService<DittoShell.Domain.Abstractions.IClipReader>(),
                    sp.GetService<DittoShell.Domain.Abstractions.IClipWriter>(),
                    sp.GetService<DittoShell.Domain.Abstractions.ITagStore>()));

            // 旁路库：标签 / 来源 / AI 缓存
            var sidecar = new SidecarStore();
            try { sidecar.InitializeAsync().GetAwaiter().GetResult(); } catch { }
            sc.AddSingleton(sidecar);
            sc.AddSingleton<ITagStore>(sidecar);
            sc.AddSingleton<ISourceStore>(sidecar);
            sc.AddSingleton<IOrganizeLogStore>(sidecar);
            sc.AddSingleton<DittoShell.Application.Services.TagService>();
            sc.AddSingleton<DittoShell.Application.Services.ExportService>();
            sc.AddSingleton<DittoShell.Application.Services.OrganizeService>();
            sc.AddSingleton<DittoShell.Application.Services.SourceTrackingService>();

            var settingsGw = new DittoSettingsGateway(located.SettingsPath);
            sc.AddSingleton<ISettingsGateway>(settingsGw);
            sc.AddSingleton(new DittoShell.Application.Services.SettingsService(
                settingsGw, backup, located.SettingsPath));
        }
        else
        {
            sc.AddSingleton<IClipReader, StubClipReader>();
        }
        // ───────── AI / 凭据 ─────────
        var credStore = new DittoShell.Infrastructure.Security.CredentialStore();
        var detector = new DittoShell.Application.Services.SensitiveDetector();
        sc.AddSingleton<ICredentialStore>(credStore);
        sc.AddSingleton(detector);
        sc.AddSingleton<IAiProvider>(_ => new DittoShell.Infrastructure.Ai.DeepSeekClient(
            () => (Config.AiBaseUrl, Config.AiModel, credStore.Read(Config.AiKeyRef))));
            sc.AddSingleton<DittoShell.Infrastructure.Sidecar.AiSessionStore>(sp =>
                new DittoShell.Infrastructure.Sidecar.AiSessionStore(
                    (DittoShell.Infrastructure.Sidecar.SidecarStore)
                        sp.GetService(typeof(DittoShell.Infrastructure.Sidecar.SidecarStore))!));

            // AI Agent：多轮工具调用
            sc.AddSingleton<DittoShell.Application.Services.AiTools>(sp =>
            {
                var rd = sp.GetService(typeof(DittoShell.Domain.Abstractions.IClipReader))
                         as DittoShell.Domain.Abstractions.IClipReader;
                var tg = sp.GetService(typeof(DittoShell.Domain.Abstractions.ITagStore))
                         as DittoShell.Domain.Abstractions.ITagStore;
                var wr = sp.GetService(typeof(DittoShell.Domain.Abstractions.IClipWriter))
                         as DittoShell.Domain.Abstractions.IClipWriter;
                var srcs = sp.GetService(typeof(DittoShell.Domain.Abstractions.ISourceStore))
                           as DittoShell.Domain.Abstractions.ISourceStore;
                return new DittoShell.Application.Services.AiTools(rd!, tg, wr, srcs);
            });

            sc.AddSingleton<DittoShell.Application.Services.AiAgentService>(sp =>
            {
                var tools = (DittoShell.Application.Services.AiTools)
                    sp.GetService(typeof(DittoShell.Application.Services.AiTools))!;

                var client = new DittoShell.Infrastructure.Ai.AiAgentClient(() =>
                    (Config.AiBaseUrl, Config.AiModel, credStore.Read(Config.AiKeyRef) ?? ""));

                var agent = new DittoShell.Application.Services.AiAgentService(
                    (msgs, tls, maxTok, temp, ct) => client.ChatAsync(msgs, tls, maxTok, temp, ct),
                    tools);

                agent.ContextBudget = App.Config.AiContextBudget;
                agent.KeepAfterCompress = App.Config.AiKeepAfterCompress;
                return agent;
            });

        sc.AddSingleton(sp => new DittoShell.Application.Services.AiService(
            sp.GetRequiredService<IAiProvider>(),
            sp.GetRequiredService<DittoShell.Application.Services.SensitiveDetector>(),
            () => Config.AiEnabled));

        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindowView>();
        sc.AddSingleton<QuickPanelViewModel>();
        sc.AddSingleton<QuickPanelView>();
        sc.AddSingleton<WindowManager>();
        ServiceProvider = sc.BuildServiceProvider();

        WindowsManager = ServiceProvider.GetRequiredService<WindowManager>();

        // ───────── 截图模式（开发用）─────────
        var shotIdx = Array.IndexOf(e.Args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length)
        {
            IsShotMode = true;
            var path = e.Args[shotIdx + 1];
            var target = shotIdx + 2 < e.Args.Length ? e.Args[shotIdx + 2] : "main";

            var qIdx = Array.IndexOf(e.Args, "--query");
            if (qIdx >= 0 && qIdx + 1 < e.Args.Length) _pendingQuery = e.Args[qIdx + 1];
            if (e.Args.Contains("dark")) ThemeManager.Apply("dark");
            try { await RunShotMode(path, target); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SHOT FAILED: " + ex);
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
                Shutdown();
            }
            return;
        }

        // ───────── 来源应用记录（轻量剪贴板监听）─────────
        StartSourceTracking();

        // ───────── 旁路库孤儿对账（后台，不阻塞启动）─────────
        _ = ReconcileSidecarAsync();

        // ───────── 正常启动 ─────────
        // --no-hotkey：调试/测试时不注册全局热键，避免冲突弹窗阻塞
        if (!e.Args.Contains("--no-hotkey")) StartHotkeys();
        CreateTray();

        // B1 阶段：启动即显示主界面，方便查看；正式版改为只驻留托盘
        WindowsManager.ShowMainWindow();
    }

    /// <summary>比对 Ditto.db 与 sidecar，清理指向已删条目的标签/来源记录。</summary>
    private async Task ReconcileSidecarAsync()
    {
        try
        {
            var reader = ServiceProvider.GetService(typeof(IClipReader)) as IClipReader;
            var sidecar = ServiceProvider.GetService(typeof(SidecarStore)) as SidecarStore;
            if (reader is null || sidecar is null) return;

            var ids = await reader.GetAllClipIdsAsync();
            var valid = new HashSet<long>(ids);

            // 取 sidecar 里出现过的 clip_id（用 max+1 之外的方式：直接扫 clip_tag/clip_meta）
            var tagged = await sidecar.GetTaggedClipIdsAsync();

            var orphans = tagged.Where(id => !valid.Contains(id)).ToList();
            if (orphans.Count > 0)
            {
                await sidecar.PurgeOrphansAsync(orphans);
                System.Diagnostics.Debug.WriteLine($"[reconcile] purged {orphans.Count} orphan clips");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[reconcile] failed: " + ex.Message);
        }
    }

    private ClipboardWatcher? _watcher;

    /// <summary>监听剪贴板变化，把来源应用记到 sidecar。捕获本身仍由 Ditto 负责。</summary>
    private void StartSourceTracking()
    {
        try
        {
            var tracking = ServiceProvider.GetService(
                typeof(DittoShell.Application.Services.SourceTrackingService))
                as DittoShell.Application.Services.SourceTrackingService;
            if (tracking is null) return;

            _watcher = new ClipboardWatcher { DelayMs = 900 };
            _watcher.Captured += snap =>
            {
                _ = tracking.HandleSnapshotAsync(snap.Text, snap.ProcessName, snap.WindowTitle);
            };

            if (!_watcher.Start())
            {
                System.Diagnostics.Debug.WriteLine("[source] clipboard listener failed to start");
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[source] " + ex.Message);
        }
    }

    // ═══════════════ 全局热键 ═══════════════

    private void StartHotkeys()
    {
        Hotkeys = new HotkeyService();
        Hotkeys.QuickPanelPressed += () => WindowsManager?.ToggleQuickPanel();
        Hotkeys.MainWindowPressed += () => WindowsManager?.ToggleMainWindow();

        HotkeySvc = Hotkeys;
        bool okQuick = Hotkeys.RegisterQuickPanel(Config.QuickPanelHotkey);
        bool okMain = Hotkeys.RegisterMainWindow(Config.MainWindowHotkey);

        if (!okQuick || !okMain)
        {
            System.Diagnostics.Debug.WriteLine(Hotkeys.LastError);

            // 热键冲突：给出可操作的中文说明（截图模式不打扰）
            if (!IsShotMode)
            {
                var detail = Hotkeys.LastError ?? "快捷键注册失败";
                var nl = Environment.NewLine;
                var msg = detail + nl + nl
                        + "最常见的原因：Ditto 自己正在使用 Ctrl+` 这个键。" + nl + nl
                        + "解决办法：" + nl
                        + "1. 打开 Ditto → 选项(Options) → 快捷键(Keyboard Shortcuts)" + nl
                        + "2. 把显示 Ctrl+` 的那一项清空（按 Backspace）" + nl
                        + "3. 点确定，然后重启 Ditto Shell" + nl + nl
                        + "改完后快速面板才能用 Ctrl+` 呼出。";
                MessageBox.Show(msg, "呼出快捷键被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ═══════════════ 托盘 ═══════════════

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Ditto Shell",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("tray.openQuick"), null, (_, _) => WindowsManager?.ToggleQuickPanel());
        menu.Items.Add(L.T("tray.openMain"), null, (_, _) => WindowsManager?.ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.settings"), null, (_, _) => WindowsManager?.ShowSettings());
        menu.Items.Add(L.T("tray.classic"), null, (_, _) => { /* B4 接通 Ditto 原界面 */ });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => Shutdown());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => WindowsManager?.ToggleQuickPanel();
    }

    // ═══════════════ 截图模式 ═══════════════

    private async Task RunShotMode(string path, string target)
    {
        Window win;
        switch (target)
        {
            case "quick":
            case "group":
            case "slash":
            {
                var q = ServiceProvider.GetRequiredService<QuickPanelView>();
                win = q;
                var vm = (QuickPanelViewModel)q.DataContext;
                vm.Reload();
                q.Show();
                q.Left = 200; q.Top = 120;
                if (target == "group") { vm.IsGroupOpen = true; q.UpdateGroupWidth(true); }
                if (target == "slash") { vm.IsSlashOpen = true; vm.SearchText = "/"; }
                break;
            }
            case "settings":
            case "progress":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m;
                m.Show();
                m.Left = 120; m.Top = 80;
                m.GoToSettings();
                if (target == "progress") m.GoToProgress();
                break;
            }
            case "viewer":
            {
                var reader = ServiceProvider.GetRequiredService<IClipReader>();
                var items = await reader.GetLatestAsync(80, null);
                var img = items.FirstOrDefault(x => x.Kind == ClipKind.Image);
                if (img is not null)
                {
                    var dib = await reader.GetRawFormatAsync(img.Id, "CF_DIB");
                    var bmp = dib is null ? null : DibDecoder.Decode(dib, 0);
                    if (bmp is not null)
                    {
                        var v = new ImageViewerWindow(bmp, $"图片 · ID {img.Id}");
                        v.Show();
                        v.Left = 90; v.Top = 60;
                        v.Activate();
                        win = v;
                        break;
                    }
                }
                var empty = new Window { Width = 200, Height = 120 };
                empty.Show();
                win = empty;
                break;
            }
            case "hovercard":
            {
                var r2 = ServiceProvider.GetRequiredService<IClipReader>();
                var list = await r2.GetLatestAsync(80, null);
                var target2 = list.FirstOrDefault(x => x.Kind == ClipKind.Image) ?? list.FirstOrDefault();
                ClipDetail? det = target2 is null ? null : await r2.GetDetailAsync(target2.Id);
                System.Windows.Media.Imaging.BitmapSource? bmp2 = null;
                if (target2 is not null && target2.Kind == ClipKind.Image)
                {
                    var dib2 = await r2.GetRawFormatAsync(target2.Id, "CF_DIB");
                    if (dib2 is not null) bmp2 = DibDecoder.Decode(dib2, 640);
                }
                var host = new Window
                {
                    Width = 420, Height = 540, WindowStyle = WindowStyle.None,
                    AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                var card = new HoverPreview();
                if (det is not null)
                    card.Show(det, bmp2,
                              System.Windows.Media.Brushes.MediumVioletRed,
                              "图片", "57 分钟前");
                host.Content = card;
                host.Left = 160; host.Top = 80;
                host.Show();
                win = host;
                break;
            }
            case "rich":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 80; m.Top = 60;

                var vm0 = (MainViewModel)m.DataContext;
                await vm0.LoadAsync();

                // 9810 是带 HTML Format 的条目
                var row = vm0.Items.FirstOrDefault(x => x.Id == 9810) ?? vm0.Items.FirstOrDefault();
                if (row is not null)
                {
                    vm0.OpenPreviewCommand.Execute(row);
                    await Task.Delay(800);
                    vm0.ToggleRichCommand.Execute(null);
                    await Task.Delay(1200);
                    System.Diagnostics.Debug.WriteLine($"[test] rich={vm0.PreviewIsRich} hasRich={vm0.PreviewHasRichText} htmlLen={vm0.PreviewHtml.Length}");
                }
                break;
            }
            case "cmd-today":
            case "cmd-images":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;

                var vm0 = (MainViewModel)m.DataContext;
                await vm0.LoadAsync();
                await Task.Delay(300);

                // 模拟：输入 "/" 打开面板 → 选命令 → 执行
                vm0.SearchText = target == "cmd-today" ? "/to" : "/im";
                await Task.Delay(400);

                var cmd = vm0.Commands.FirstOrDefault();
                if (cmd is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[test] running {cmd.Name}");
                    vm0.RunCommand(cmd);
                    await Task.Delay(800);
                    System.Diagnostics.Debug.WriteLine($"[test] after: {vm0.Items.Count} items, {vm0.StatusText}, title={vm0.Title}");
                }
                break;
            }
            case "quickdetail":
            {
                var q = ServiceProvider.GetRequiredService<QuickPanelView>();
                win = q;
                var vm = (QuickPanelViewModel)q.DataContext;
                vm.Reload();
                q.Show(); q.Left = 100; q.Top = 100;
                await Task.Delay(500);
                // 选一条长文本，验证详情是不是全文
                var longRow = vm.Items.FirstOrDefault(x => (x.Preview?.Length ?? 0) > 100) ?? vm.Items.FirstOrDefault();
                if (longRow is not null) vm.Selected = longRow;
                await Task.Delay(600);
                vm.IsDetailOpen = true;
                q.UpdateDetailWidth(true);
                await Task.Delay(700);
                break;
            }
            case "backup":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 100; m.Top = 60;
                m.GoToSettingsTab("backup");
                await Task.Delay(700);
                break;
            }
            case "sidebar":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 100; m.Top = 60;
                m.GoToSettingsTab("sidebar");
                await Task.Delay(700);
                break;
            }
            case "sessionlist":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 60; m.Top = 40; m.Width = 1200;
                var vms = (MainViewModel)m.DataContext;
                await vms.LoadAsync();
                await Task.Delay(300);
                m.SetAiDrawer(true);
                await Task.Delay(300);
                await vms.AiChat.RefreshSessionsAsync();
                vms.AiChat.IsSessionListOpen = true;
                await Task.Delay(600);
                break;
            }
            case "auditmenus":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 60; m.Top = 40;
                await Task.Delay(900);

                var logPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "data", "menu.log");
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(logPath)!);
                System.IO.File.AppendAllText(logPath,
                    "=== AUDIT START " + DateTime.Now.ToString("HH:mm:ss") + " ===\n");

                // 1) 主窗口所有 ContextMenu
                int hooked = 0, unhooked = 0;
                void ScanMenus(System.Windows.DependencyObject node, string where)
                {
                    if (node is System.Windows.FrameworkElement fe)
                    {
                        var cm = fe.ContextMenu;
                        if (cm is not null)
                        {
                            var hasOpened = cm.GetType()
                                .GetEvent("Opened") is not null;
                            System.IO.File.AppendAllText(logPath,
                                $"MENU {where} type={fe.GetType().Name} " +
                                $"placement={cm.Placement} width={cm.MaxWidth}\n");
                            hooked++;
                        }
                    }

                    var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++)
                        ScanMenus(System.Windows.Media.VisualTreeHelper.GetChild(node, i), where);
                }
                ScanMenus(m, "MainWindow");

                // 2) TextBox 的 ContextMenu 是否已禁用
                int tbTotal = 0, tbWithMenu = 0;
                void ScanTextBox(System.Windows.DependencyObject node)
                {
                    if (node is System.Windows.Controls.TextBox tb)
                    {
                        tbTotal++;
                        if (tb.ContextMenu is not null) tbWithMenu++;
                    }
                    var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++)
                        ScanTextBox(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
                ScanTextBox(m);

                System.IO.File.AppendAllText(logPath,
                    $"SUMMARY menus={hooked} textboxes={tbTotal} " +
                    $"tbWithSystemMenu={tbWithMenu}\n");

                Console.WriteLine($"AUDIT menus={hooked} textboxes={tbTotal} tbWithMenu={tbWithMenu}");

                // 3) 打开第一个真实菜单，验证 Opened="Menu_Opened" 是否触发
                System.Windows.Controls.ContextMenu? firstMenu = null;
                void FindFirst(System.Windows.DependencyObject node)
                {
                    if (firstMenu is not null) return;
                    if (node is System.Windows.FrameworkElement fe && fe.ContextMenu is not null)
                    {
                        firstMenu = fe.ContextMenu;
                        return;
                    }
                    var c = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < c; i++)
                        FindFirst(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
                FindFirst(m);

                if (firstMenu is not null)
                {
                    System.IO.File.AppendAllText(logPath,
                        $"REALMENU opening placement={firstMenu.Placement}\n");

                    SetCursorPos(600, 400);
                    await Task.Delay(120);

                    firstMenu.PlacementTarget = m;
                    firstMenu.IsOpen = true;
                    await Task.Delay(700);

                    if (PresentationSource.FromVisual(firstMenu)
                        is System.Windows.Interop.HwndSource src2)
                    {
                        GetWindowRect(src2.Handle, out var r2);
                        System.IO.File.AppendAllText(logPath,
                            $"REALMENU actual=({r2.Left},{r2.Top}) " +
                            $"expect=(600,400) match={(Math.Abs(r2.Left - 600) < 8)}\n");
                        Console.WriteLine($"REALMENU actual=({r2.Left},{r2.Top})");
                    }

                    firstMenu.IsOpen = false;
                }
                else
                {
                    System.IO.File.AppendAllText(logPath, "REALMENU none found\n");
                }
                await Task.Delay(200);
                break;
            }
            case "menuverify":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 80; m.Top = 60;
                m.Width = 1200; m.Height = 700;
                await Task.Delay(800);

                var logPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "data", "menu.log");
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(logPath)!);

                // 测试坐标（屏幕）——先把光标移过去，否则测到的是别处
                const int targetX = 600;
                const int targetY = 400;
                SetCursorPos(targetX, targetY);
                await Task.Delay(150);

                // 造一个 ContextMenu 验证 Placement=Mouse 的实际落点
                var menu = new System.Windows.Controls.ContextMenu();
                menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "测试项" });

                // 模拟真实场景：设 PlacementTarget（挂到窗口里的某个元素上）
                menu.Opened += (s4, e4) => MenuOpened(menu, e4);
                menu.PlacementTarget = m;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
                menu.IsOpen = true;

                await Task.Delay(900);

                // 读菜单 HWND 的真实位置
                try
                {
                    if (PresentationSource.FromVisual(menu)
                        is System.Windows.Interop.HwndSource src)
                    {
                        GetWindowRect(src.Handle, out var r);
                        var msg = $"VERIFY target=({targetX},{targetY}) actual=({r.Left},{r.Top}) " +
                                  $"size=({r.Right - r.Left}x{r.Bottom - r.Top}) " +
                                  $"match={(Math.Abs(r.Left - targetX) < 6 && Math.Abs(r.Top - targetY) < 6)}";
                        System.IO.File.AppendAllText(logPath, msg + Environment.NewLine);
                        Console.WriteLine(msg);
                    }
                    else
                    {
                        System.IO.File.AppendAllText(logPath,
                            "VERIFY no HwndSource" + Environment.NewLine);
                        Console.WriteLine("VERIFY no HwndSource");
                    }
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(logPath,
                        "VERIFY ERROR " + ex.Message + Environment.NewLine);
                }

                menu.IsOpen = false;
                await Task.Delay(200);
                break;
            }
            case "pinvoketest":
            {
                var m0 = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m0; m0.Show();
                // 验证 GetCursorPos P/Invoke 能正常调用
                try
                {
                    await Task.Delay(300);

                    var logPath = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "data", "menu.log");
                    System.IO.Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(logPath)!);

                    // 反射调用私有方法验证
                    var t = typeof(MainWindowView);
                    var m1 = t.GetMethod("GetCursorPosRaw",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static);
                    if (m1 is null)
                    {
                        System.IO.File.AppendAllText(logPath,
                            "GETCURSORPOS RAW: method not found\n");
                    }
                    else
                    {
                        var args = new object[] { null! };
                        var ok = (bool)m1.Invoke(null, args)!;
                        System.IO.File.AppendAllText(logPath,
                            $"GETCURSORPOS OK={ok} ptr={(args[0] is null ? "null" : "set")}\n");
                    }
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "data", "menu.log"),
                        "PINVOKE TEST FAILED: " + ex.Message + "\n");
                }

                await Task.Delay(200);
                break;
            }
            case "menutest":
            {
                // 打开主窗口，程序化弹出右键菜单，全屏截图验证位置
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 80; m.Top = 60;
                m.Width = 1200; m.Height = 700;
                await Task.Delay(700);

                // 找到列表第一个条目，弹它的 ContextMenu
                var list = m.FindName("ItemsList") as System.Windows.Controls.ListBox;
                if (list is not null && list.Items.Count > 3)
                {
                    list.SelectedIndex = 1;

                    // 让 ListBox 生成容器
                    list.UpdateLayout();

                    var container = list.ItemContainerGenerator
                        .ContainerFromIndex(1) as System.Windows.FrameworkElement;

                    if (container is not null)
                    {
                        // 找到 container 里的 Border（挂 ContextMenu 的那个）
                        System.Windows.Controls.ContextMenu? menu = null;
                        void FindMenu(System.Windows.DependencyObject node)
                        {
                            if (menu is not null) return;
                            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                            for (int i = 0; i < count; i++)
                            {
                                var ch = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                                if (ch is System.Windows.FrameworkElement fe && fe.ContextMenu is not null)
                                {
                                    menu = fe.ContextMenu;
                                    return;
                                }
                                FindMenu(ch);
                            }
                        }
                        FindMenu(container);

                        if (menu is not null)
                        {
                            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
                            menu.HorizontalOffset = 400;
                            menu.VerticalOffset = 300;
                            menu.StaysOpen = true;      // 测试模式下别自动关
                            menu.IsOpen = true;
                            Console.WriteLine($"menu opened={menu.IsOpen} at 400,300");

                            // 等 WPF 完成 popup 定位
                            await Dispatcher.InvokeAsync(() => { },
                                System.Windows.Threading.DispatcherPriority.ContextIdle);
                            await Task.Delay(800);
                            Console.WriteLine($"menu still open={menu.IsOpen}");
                        }
                        else
                        {
                            Console.WriteLine("menu not found on container");
                        }
                    }
                }

                // 全屏截图（包含 Popup）
                try
                {
                    var screen = System.Windows.Forms.Screen.PrimaryScreen;
                    if (screen is not null)
                    {
                        var b = screen.Bounds;
                        using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
                        using var g = System.Drawing.Graphics.FromImage(bmp);
                        g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("screenshot saved: " + path);
                    }
                }
                catch (Exception ex) { Console.WriteLine("screenshot failed: " + ex.Message); }

                await Task.Delay(300);
                break;
            }
            case "aidrawer":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 60; m.Top = 40;
                m.Width = 1200;
                var vmd = (MainViewModel)m.DataContext;
                await vmd.LoadAsync();
                await Task.Delay(400);
                m.SetAiDrawer(true);
                await Task.Delay(300);
                vmd.AiChat.InputText = "上周的改枪码配置";
                await Task.Delay(500);
                break;
            }
            case "quickfilter":
            {
                var q = ServiceProvider.GetRequiredService<QuickPanelView>();
                win = q;
                var vmq = (QuickPanelViewModel)q.DataContext;
                vmq.Reload();
                q.Show(); q.Left = 100; q.Top = 100;
                await Task.Delay(500);
                vmq.IsSlashOpen = false;
                vmq.IsFilterBarOpen = true;
                vmq.FilterTab = "type";
                vmq.LoadFilterKindsPublic();
                await Task.Delay(600);
                break;
            }
            case "slashmain":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 80; m.Top = 50;
                var vms = (MainViewModel)m.DataContext;
                await vms.LoadAsync();
                await Task.Delay(400);
                vms.SearchText = "/";
                vms.BuildSlashItemsPublic();
                vms.IsSlashOpen = true;
                await Task.Delay(600);
                break;
            }
            case "suggest":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 100; m.Top = 60;
                var vm3 = (MainViewModel)m.DataContext;
                await vm3.LoadAsync();
                await Task.Delay(400);
                vm3.SearchText = "tag:";
                await vm3.UpdateSuggestionsAsync("tag:");
                await Task.Delay(600);
                break;
            }
            case "filterpanel":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 100; m.Top = 60;
                var vm4 = (MainViewModel)m.DataContext;
                await vm4.LoadAsync();
                await Task.Delay(400);
                vm4.IsFilterPanelOpen = true;
                await Task.Delay(600);
                break;
            }
            case "explain":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 100; m.Top = 60;
                var vm2 = (MainViewModel)m.DataContext;
                await vm2.LoadAsync();
                await Task.Delay(400);
                vm2.AddFilterTag("tag:dittoshell");
                vm2.SearchText = "报告";
                await Task.Delay(500);
                vm2.IsExplainOpen = true;
                await Task.Delay(600);
                break;
            }
            case "tagpanel":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;
                var vm0 = (MainViewModel)m.DataContext;
                await vm0.LoadAsync();
                await Task.Delay(500);
                vm0.IsTagPanelOpen = true;
                await Task.Delay(600);
                break;
            }
            case "tagprefix":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;
                var vm0 = (MainViewModel)m.DataContext;
                await vm0.LoadAsync();
                await Task.Delay(300);

                // 模拟：输入 /tag → 选中 → 前缀 chip 出现
                vm0.SearchText = "/tag";
                await Task.Delay(400);
                var cmd = vm0.Commands.FirstOrDefault(c => c.Name == "/tag");
                if (cmd is not null) vm0.RunCommand(cmd);
                await Task.Delay(600);
                break;
            }
            case "view-secret":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;
                var vm0 = (MainViewModel)m.DataContext;
                await vm0.LoadAsync();
                var v = vm0.Views.FirstOrDefault(x => x.Id == -7);
                if (v is not null)
                {
                    vm0.SelectViewCommand.Execute(v);
                    await Task.Delay(700);
                }
                break;
            }
            case "view-today":
            case "view-images":
            case "view-code":
            case "view-uncat":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;

                var vm0 = (MainViewModel)m.DataContext;
                var idx = target switch
                {
                    "view-today" => 1,
                    "view-images" => 2,
                    "view-code" => 4,
                    "view-uncat" => 5,
                    _ => 0
                };

                // 等首屏数据加载完再模拟点击
                await vm0.LoadAsync();
                if (idx < vm0.Views.Count)
                {
                    var picked = vm0.Views[idx];
                    vm0.SelectViewCommand.Execute(picked);
                    await Task.Delay(600);
                    System.Diagnostics.Debug.WriteLine($"[test] clicked {picked.Name}, now {vm0.Items.Count} items, status={vm0.StatusText}");
                }
                break;
            }
            case "hotkeys":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;
                m.GoToSettingsTab("hotkeys");
                break;
            }
            case "appearance":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m; m.Show(); m.Left = 120; m.Top = 80;
                m.GoToSettingsTab("appearance");
                break;
            }
            case "organize":
            {
                var w = new AiOrganizeWindow();
                win = w; w.Show(); w.Left = 150; w.Top = 90;
                break;
            }
            case "ai-settings":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m;
                m.Show();
                m.Left = 120; m.Top = 80;
                m.GoToAiSettings();
                break;
            }
            case "ditto-settings":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m;
                m.Show();
                m.Left = 120; m.Top = 80;
                m.GoToDittoSettings();
                break;
            }
            case "stats":
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m;
                m.Show();
                m.Left = 120; m.Top = 80;
                m.GoToStats();
                break;
            }
            default:
            {
                var m = ServiceProvider.GetRequiredService<MainWindowView>();
                win = m;
                m.Show();
                m.Left = 120; m.Top = 80;
                if (!string.IsNullOrEmpty(_pendingQuery))
                {
                    var vm2 = (MainViewModel)m.DataContext;
                    vm2.SearchText = _pendingQuery;
                    _ = vm2.SearchCommand.ExecuteAsync(null);
                }
                if (target == "main-expand") m.ExpandPreviewForShot();
                break;
            }
        }

        await Task.Delay(1200);
        Capture(win, path);
    }

    private void Capture(Window win, string path)
    {
        try
        {
            win.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(win);
            int w = (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

            // 用窗口内容（避开半透明窗口背景导致的黑底）
            rtb.Render(win);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".err.txt", ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════ 退出 ═══════════════

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;

        _watcher?.Dispose();
        _watcher = null;

        _tray?.Dispose();
        _tray = null;
        Hotkeys?.Dispose();
        Hotkeys = null;

        // 强制关闭所有窗口（否则 OnClosing 的 Hide 会阻止退出）
        var wins = new List<Window>();
        foreach (Window w in Windows) wins.Add(w);
        foreach (var w in wins)
        {
            try { w.Close(); } catch { }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
