# 07 · 扩展性

> 只预留**真正需要的**扩展点。不做插件系统（见 D04）。

---

## 1. AI 厂商可换

```csharp
public interface IAiProvider
{
    string Name { get; }                       // "DeepSeek" / "Ollama" / "OpenAI"
    Task<AiResult> CompleteAsync(AiRequest req, CancellationToken ct);
    Task<bool> TestConnectionAsync(CancellationToken ct);
}

public sealed record AiRequest(
    string SystemPrompt,
    string UserContent,
    int MaxTokens = 2000,
    float Temperature = 0.3f);

public sealed record AiResult(
    bool Success,
    string? Content,
    string? ErrorMessage,
    int ElapsedMs);
```

**新增厂商** = 实现一个类 + 在 DI 注册 + 加一条预设配置。不改任何调用方。

| 预设 | base_url | 默认 model |
|---|---|---|
| DeepSeek（默认） | `https://api.deepseek.com` | `deepseek-flash` |
| DeepSeek Pro | 同上 | `deepseek-v4-pro` |
| OpenAI 兼容（自填） | 用户输入 | 用户输入 |
| 本地 Ollama | `http://localhost:11434/v1` | `qwen2.5:7b` 之类 |

---

## 2. 语言可加

```
App/Resources/
├─ Strings.zh-CN.json
├─ Strings.en.json
└─ Strings.{新增}.json        ← 丢一个文件进去就生效
```

```json
{
  "quickPanel.searchPlaceholder": "搜索历史…",
  "quickPanel.manageHistory": "管理历史",
  "group.newSubGroup": "新建子组",
  "error.dbBusy": "Ditto 正忙，请稍后再试"
}
```

规则：
- 代码里**零硬编码文案**
- 缺失 key 时回落英文并记一条 warning 日志
- 语言选择：跟随系统 → 用户手动覆盖

---

## 3. 主题可换

所有颜色集中在 `Themes/Tokens.xaml`：

```xml
<Color x:Key="Primary">#1677FF</Color>
<Color x:Key="Brand">#722ED1</Color>
<SolidColorBrush x:Key="BgBrush" Color="{StaticResource Bg}"/>
```

| 主题 | 文件 | 状态 |
|---|---|---|
| 浅色 | `Themes/Light.xaml` | v1 |
| 深色 | `Themes/Dark.xaml` | v1 |
| 自定义 | 预留 | 不做（v2 可考虑） |

新增主题 = 复制一份 Tokens 改色值，不动布局。

---

## 4. 数据源可换（预留，不实现）

```csharp
// 只定义接口，不实现。为未来"接别的剪贴板管理器"留门。
public interface IClipSource
{
    string Name { get; }
    Task<IReadOnlyList<ClipItem>> GetLatestAsync(int limit, CancellationToken ct);
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
}
```
`DittoClipSource` 是唯一实现。**v1 不写第二个**，避免过度设计。

---

## 5. 配置项（用户可改）

`data/config.json`：

```json
{
  "version": 1,
  "dittoPath": "C:/Program Files/Ditto/Ditto.exe",
  "language": "zh-CN",
  "theme": "light",
  "hotkeys": { "quickPanel": "Ctrl+`", "mainWindow": "Ctrl+Shift+`" },
  "ai": {
    "enabled": false,
    "provider": "deepseek",
    "baseUrl": "https://api.deepseek.com",
    "model": "deepseek-flash",
    "keyRef": "DittoShell/DeepSeek"
  },
  "privacy": {
    "neverUploadGroups": [],
    "maskSensitiveInList": true
  },
  "ui": {
    "quickPanelRememberLastGroup": true,
    "rowHeight": 46
  }
}
```

配置文件损坏 → 用默认值重建 + 备份坏文件 + 提示。

---

## 6. 明确不做（写死）

| 不做 | 原因 |
|---|---|
| 插件 DLL 加载 | 单人项目纯成本；需要时用 Ditto 原生 Addin |
| 脚本引擎（类似 Ditto 的 ChaiScript） | 同样理由 |
| 对外 HTTP API | 无需求 |
| 多用户 / 权限分级 | 单用户本地工具 |
| 云配置同步 | 与 D04 冲突 |
