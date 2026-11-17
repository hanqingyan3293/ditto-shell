# 03 · 架构设计

> 4 层单向依赖。任何跨层调用视为缺陷。

---

## 1. 分层图

```
┌─────────────────────────────────────────────────────────────┐
│ L1  Presentation  (WPF XAML — 只做界面与绑定)                │
│   QuickPanelView · MainWindowView · SettingsView             │
│   GroupFlyoutView · PreviewPaneView · SlashPanelView         │
├─────────────────────────────────────────────────────────────┤
│ L2  ViewModel  (MVVM — 不引用 System.Windows)                │
│   QuickPanelVM · MainVM · GroupVM · SettingsVM               │
│   SearchVM · PreviewVM · SlashPanelVM · StatsVM              │
├─────────────────────────────────────────────────────────────┤
│ L3  Application  (用例编排 — 纯 C#，可单测)                   │
│   SearchService · PasteService · GroupService                │
│   SettingsService · ExportService · AiService                │
│   BackupService · IntegrityService · SensitiveDetector       │
├─────────────────────────────────────────────────────────────┤
│ L4  Infrastructure  (唯一接触外部世界)                        │
│   DittoDbReader · DittoDbWriter · DittoBridge(IPC)           │
│   DittoSettingsGateway · SidecarStore · ThumbnailCache       │
│   DeepSeekClient · HotkeyHost · I18nProvider                 │
│   Logger · BackupStore                                       │
├─────────────────────────────────────────────────────────────┤
│ L0  Domain  (纯模型 + 接口定义，零第三方依赖)                  │
│   ClipItem · ClipDetail · GroupNode · Tag · SearchQuery      │
│   IClipReader · IClipWriter · IDittoBridge · IAiProvider …   │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. 解决方案结构

```
src/
├─ DittoShell.sln
├─ DittoShell.Domain/                     ← L0
│  ├─ Models/           ClipItem.cs, GroupNode.cs, Tag.cs, SearchQuery.cs
│  └─ Abstractions/     IClipReader.cs, IClipWriter.cs, IDittoBridge.cs,
│                       ISettingsGateway.cs, IAiProvider.cs, ILogger.cs
│
├─ DittoShell.Application/                ← L3
│  └─ Services/         SearchService.cs, PasteService.cs, GroupService.cs,
│                       SettingsService.cs, ExportService.cs, AiService.cs,
│                       BackupService.cs, IntegrityService.cs, SensitiveDetector.cs
│
├─ DittoShell.Infrastructure/             ← L4
│  ├─ DittoDb/          DittoDbReader.cs, DittoDbWriter.cs, Sql/ (查询语句常量)
│  ├─ Ipc/              DittoBridge.cs, Win32.cs (P/Invoke)
│  ├─ Settings/         DittoSettingsGateway.cs (UTF-16 INI)
│  ├─ Sidecar/          SidecarStore.cs, Migrations/ (V1__init.sql …)
│  ├─ Media/            ThumbnailCache.cs, DibDecoder.cs
│  ├─ Ai/               DeepSeekClient.cs
│  ├─ Platform/         HotkeyHost.cs, DpiHelper.cs, WindowEffects.cs
│  └─ Diagnostics/      FileLogger.cs
│
├─ DittoShell.App/                        ← L1 + L2
│  ├─ App.xaml(.cs)                       ← 启动、DI 注册
│  ├─ Views/           (XAML)
│  ├─ ViewModels/
│  ├─ Resources/
│  │  ├─ Strings.zh-CN.json
│  │  ├─ Strings.en.json
│  │  └─ Themes/       Light.xaml, Dark.xaml, Tokens.xaml
│  └─ Converters/
│
└─ DittoShell.Tests/                      ← xUnit
```

**项目引用方向**（单向）：

```
App → Application → Domain
App → Infrastructure → Domain
Application → Domain
Infrastructure → Domain
```

`Domain` 不引用任何东西。`Application` 不引用 `Infrastructure`（只依赖接口，靠 DI 注入）。

---

## 3. 模块职责

| 模块 | 层 | 只做 | 绝不做 |
|---|---|---|---|
| `DittoDbReader` | L4 | 只读查询、分页、LIKE 搜索、详情读取 | 写、建表、改 PRAGMA |
| `DittoDbWriter` | L4 | 改 4 个字段、新建/改名/删除分组 | 删行、碰 `Data` 表、改表结构 |
| `DittoBridge` | L4 | `/paste:<id>`、`SendMessage(WM_*)`、`/close` | 模拟键鼠、注入、hook |
| `DittoSettingsGateway` | L4 | 按行读写 UTF-16 INI | 重写整个文件、删未知键 |
| `SidecarStore` | L4 | 标签、来源、AI 缓存、schema 迁移 | 存剪贴板正文 |
| `ThumbnailCache` | L4 | 解码 DIB、LRU 内存缓存 | 落盘 |
| `DeepSeekClient` | L4 | OpenAI 兼容 HTTP 调用 | 缓存响应正文、后台批量上传 |
| `HotkeyHost` | L4 | `RegisterHotKey` + 事件 | 处理业务 |
| `SearchService` | L3 | 解析查询语法、组合多源结果、排序 | 直接摸数据库 |
| `PasteService` | L3 | 选格式 → 调 Bridge → 记录 | 自己 SetClipboardData |
| `GroupService` | L3 | 增删改校验、备份编排、刷新通知 | 拼 SQL |
| `AiService` | L3 | 组装提示词、脱敏、结果解析 | 自动执行写操作 |
| `SensitiveDetector` | L3 | 本地正则识别 token/密码/身份证 | 联网校验 |
| `IntegrityService` | L3 | 启动自检、孤儿对账、版本迁移 | 修改数据 |

---

## 4. 依赖规则（禁止项）

| # | 禁止 | 理由 |
|---|---|---|
| N1 | L1 直接 `new` 任何 L4 类型 | UI 换皮不能碰数据层 |
| N2 | L4 模块之间横向调用（如 `DittoDbReader` 调 `DittoBridge`） | 一切经由 L3 编排 |
| N3 | L2/L3 直接 `File.*` / `Directory.*` | 所有 IO 走 L4（统一备份/日志/错误） |
| N4 | 任何层手拼 SQL | 统一在 L4 参数化 |
| N5 | 任何层直接 `new HttpClient()` | 只允许 `DeepSeekClient` |
| N6 | `Domain` 引用任何第三方包 | 保持零依赖 |
| N7 | 在 View 的 code-behind 里写业务逻辑 | 只允许纯 UI 交互 |

**唯一例外**：`ILogger` 可被任意层注入（只写不读）。

---

## 5. 关键接口（Domain 层契约）

```csharp
// 只读
public interface IClipReader
{
    Task<IReadOnlyList<ClipItem>> GetLatestAsync(int limit, long? groupId, CancellationToken ct);
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
    Task<ClipDetail?> GetDetailAsync(long clipId, CancellationToken ct);
    Task<IReadOnlyList<GroupNode>> GetGroupTreeAsync(CancellationToken ct);
    Task<long> GetMaxClipIdAsync(CancellationToken ct);
}

// 受控写
public interface IClipWriter
{
    Task MoveToGroupAsync(IReadOnlyList<long> clipIds, long groupId, CancellationToken ct);
    Task<long> CreateGroupAsync(string name, long parentId, CancellationToken ct);
    Task RenameGroupAsync(long groupId, string newName, CancellationToken ct);
    Task DeleteGroupAsync(long groupId, CancellationToken ct);
    Task SetStickyAsync(long clipId, bool sticky, CancellationToken ct);
    Task SetKeepAsync(long clipId, bool keep, CancellationToken ct);
}

// 与 Ditto 进程交互
public interface IDittoBridge
{
    Task<bool> IsRunningAsync(CancellationToken ct);
    Task PasteAsync(long clipId, bool plainText, CancellationToken ct);
    Task RefreshViewAsync();
    Task BackupDatabaseAsync();
    Task CloseOriginalWindowAsync();
}

// 设置
public interface ISettingsGateway
{
    Task<IReadOnlyDictionary<string, string>> ReadAllAsync(CancellationToken ct);
    Task WriteAsync(IReadOnlyDictionary<string, string> changes, CancellationToken ct);
    Task<long?> GetHotkeyAsync(CancellationToken ct);   // 704 = Ctrl+`
}

// AI
public interface IAiProvider
{
    string Name { get; }
    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct);
    Task<bool> TestConnectionAsync(CancellationToken ct);
}
```

---

## 6. 启动流程

```
1. 单实例检查（Mutex）
2. 读配置 → data/config.json（不存在则用默认）
3. 定位 Ditto
   a. 读上次记录的路径
   b. 探测 4 个常见路径
   c. 失败 → 引导卡「选择 Ditto 目录」
4. 自检 IntegrityService
   a. 打开 Ditto.db（只读）→ 失败则进入只读降级
   b. 检查 sidecar 版本 → 需要则迁移
   c. 对账孤儿标签
5. 注册全局热键
   a. 检测 Ditto 是否占用同一热键 → 冲突则弹中文引导卡
6. 创建托盘图标 + 预热快速面板（隐藏窗口，预解码首屏 50 条）
7. 进入消息循环
```

**预热**：快速面板在启动时创建为隐藏窗口并填充首屏，第一次按热键只做 Show + Activate（约 30ms），不是"从零构建"。

---

## 7. 关键时序：粘贴

```
User          QuickPanelVM      PasteService      DittoBridge        Ditto.exe
 │  Enter          │                 │                 │                 │
 ├────────────────>│                 │                 │                 │
 │                 ├────────────────>│                 │                 │
 │                 │                 ├── Backup?(no) ─>│                 │
 │                 │                 ├────────────────>│  /paste:9793    │
 │                 │                 │                 ├────────────────>│
 │                 │                 │                 │  还原格式+Ctrl+V │
 │  ← 面板隐藏 ────┤                 │                 │                 │
```

---

## 8. 线程模型

| 线程 | 职责 | 规则 |
|---|---|---|
| UI 线程 | 渲染、输入 | **不做任何 IO** |
| 后台线程池 | 数据库查询、图片解码、HTTP | `async/await`，不阻塞 UI |
| 定时器（1s） | 检测新条目（`MAX(lID)` 变化） | 只在面板可见时跑 |

**取消**：所有异步方法带 `CancellationToken`，面板关闭时取消未完成查询。

---

## 9. 配置与数据文件

```
data/
├─ config.json          外壳设置（语言、主题、AI 配置引用、热键）
├─ sidecar.db           标签 / 来源 / AI 缓存
├─ logs/20261115.log    日志（滚动 7 天）
└─ thumbs/              （预留，当前不落盘）

backup/
├─ Ditto_20261115_083000.db
├─ Ditto.Settings_20261115_083000.ini
└─ sidecar_20261115_083000.db
```
