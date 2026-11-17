using DittoShell.Domain.Models;

namespace DittoShell.Domain.Abstractions;

/// <summary>只读访问 Ditto 剪贴板历史。</summary>
public interface IClipReader
{
    Task<IReadOnlyList<ClipItem>> GetLatestAsync(int limit, long? groupId, CancellationToken ct = default);
    Task<IReadOnlyList<ClipItem>> SearchAsync(
        string query, long? groupId, ClipKind? kind,
        string? tagFilter = null, string? fromFilter = null,
        CancellationToken ct = default);
    Task<ClipDetail?> GetDetailAsync(long clipId, CancellationToken ct = default);
    Task<IReadOnlyList<GroupNode>> GetGroupTreeAsync(CancellationToken ct = default);
    Task<long> GetMaxClipIdAsync(CancellationToken ct = default);
    Task<int> GetTotalCountAsync(CancellationToken ct = default);

    /// <summary>侧栏虚拟视图的计数（今天/图片/链接/未归类）。</summary>
    Task<IReadOnlyDictionary<string, int>> GetViewCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// 用 KindClassifier 扫全库，返回各类型的准确计数（启动时算一次并缓存）。
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetKindCountsAsync(CancellationToken ct = default);

    /// <summary>各分组下的条目数（用于统计页）。</summary>
    Task<IReadOnlyList<(string Name, int Count)>> GetGroupDistributionAsync(int top, CancellationToken ct = default);

    /// <summary>最近 N 天每天的条目数（用于统计图表）。</summary>
    Task<IReadOnlyList<(string Day, int Count)>> GetDailyCountsAsync(int days, CancellationToken ct = default);

    /// <summary>库文件大小（字节）。</summary>
    long GetDatabaseSizeBytes();

    /// <summary>全部条目 ID（用于旁路库孤儿对账）。</summary>
    Task<IReadOnlyList<long>> GetAllClipIdsAsync(CancellationToken ct = default);

    /// <summary>取某条目的原始剪贴板格式字节（如图片的 CF_DIB），用于解码显示。</summary>
    Task<byte[]?> GetRawFormatAsync(long clipId, string format, CancellationToken ct = default);
}

/// <summary>受控写入：只允许改分组归属 / 置顶 / 保留标记，以及分组增删改。</summary>
public interface IClipWriter
{
    Task MoveToGroupAsync(IReadOnlyList<long> clipIds, long groupId, CancellationToken ct = default);
    Task<long> CreateGroupAsync(string name, long parentId, CancellationToken ct = default);
    Task RenameGroupAsync(long groupId, string newName, CancellationToken ct = default);
    Task DeleteGroupAsync(long groupId, CancellationToken ct = default);

    /// <summary>
    /// 修改条目的文本内容（同时更新 Main.mText 和 Data 里的 CF_UNICODETEXT）。
    /// 调用方负责先备份。会刷新 Ditto 视图。
    /// </summary>
    Task UpdateTextAsync(long clipId, string newText, CancellationToken ct = default);

    /// <summary>
    /// 批量删除多个条目（只备份一次，所有删除在一个事务里）。
    /// </summary>
    Task<int> DeleteClipsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>
    /// 批量删除某个分组下的所有条目。
    /// 走 Ditto 触发器（delete_data_trigger 自动登记 MainDeletes）。
    /// 调用方负责先备份。
    /// </summary>
    Task<int> DeleteClipsInGroupAsync(long groupId, CancellationToken ct = default);

    /// <summary>
    /// 把某个分组下的所有条目移回未归类（lParentID = -1）。
    /// 安全可逆。
    /// </summary>
    Task<int> MoveAllToUngroupedAsync(long groupId, CancellationToken ct = default);

    /// <summary>
    /// 删除条目。走 Ditto 原生机制：只删 Main 行，
    /// 触发器会自动登记 MainDeletes，Ditto 空闲时清理 Data。
    /// 调用方负责先备份。
    /// </summary>
    Task DeleteClipAsync(long clipId, CancellationToken ct = default);

    /// <summary>改父级（移到别的分组下 / 提升到顶层）。</summary>
    Task ReparentGroupAsync(long groupId, long newParentId, CancellationToken ct = default);
    Task SetStickyAsync(long clipId, bool sticky, CancellationToken ct = default);
    Task SetKeepAsync(long clipId, bool keep, CancellationToken ct = default);
}

/// <summary>与 Ditto 进程交互：粘贴、刷新、备份。</summary>
public interface IDittoBridge
{
    Task<bool> IsRunningAsync(CancellationToken ct = default);
    Task PasteAsync(long clipId, bool plainText, CancellationToken ct = default);
    Task RefreshViewAsync(CancellationToken ct = default);
    Task BackupDatabaseAsync(CancellationToken ct = default);
    Task CloseOriginalWindowAsync(CancellationToken ct = default);
}

/// <summary>读写 Ditto.Settings（UTF-16LE INI）。</summary>
public interface ISettingsGateway
{
    Task<IReadOnlyDictionary<string, string>> ReadAllAsync(CancellationToken ct = default);
    Task WriteAsync(IReadOnlyDictionary<string, string> changes, CancellationToken ct = default);
    Task<long?> GetHotkeyAsync(CancellationToken ct = default);
}

/// <summary>标签与旁路数据。</summary>
public interface ITagStore
{
    Task<IReadOnlyList<string>> GetTagsAsync(long clipId, CancellationToken ct = default);
    Task AddTagAsync(long clipId, string tag, CancellationToken ct = default);
    Task RemoveTagAsync(long clipId, string tag, CancellationToken ct = default);

    /// <summary>
    /// 按标签查条目。strict=true 只返回含全部标签的；false 按命中标签数降序。
    /// 返回 (clipId, 命中数)。
    /// </summary>
    Task<List<(long ClipId, int Hits)>> GetClipIdsByTagsAsync(
        IReadOnlyList<string> tags, bool strict, CancellationToken ct = default);

    /// <summary>所有标签及其使用次数（供标签选择器）。</summary>
    Task<List<(string Name, int Count)>> GetAllTagsAsync(CancellationToken ct = default);
}

/// <summary>AI 整理日志：记住哪些条目已经整理过（避免重复上传浪费 token）。</summary>
public interface IOrganizeLogStore
{
    /// <summary>已整理过的 clip_id 集合。</summary>
    Task<HashSet<long>> GetOrganizedIdsAsync(CancellationToken ct = default);

    /// <summary>记录一条已整理。</summary>
    Task MarkOrganizedAsync(long clipId, string contentHash, string groupName, string tag, CancellationToken ct = default);

    /// <summary>内容变了要重新整理（清除记录）。</summary>
    Task ClearAsync(long clipId, CancellationToken ct = default);

    Task<int> GetOrganizedCountAsync(CancellationToken ct = default);

    /// <summary>改名历史（AI 建议名 → 用户改后的名）。</summary>
    Task<List<(string From, string To)>> GetRenameHistoryAsync(
        int limit = 20, CancellationToken ct = default);

    /// <summary>清空全部整理记录（"全部重来"模式用）。</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>整理过但内容变了的条目 ID（需要重新整理）。</summary>
    Task<List<long>> GetStaleIdsAsync(CancellationToken ct = default);
}

/// <summary>来源应用记录。</summary>
public interface ISourceStore
{
    Task RecordSourceAsync(long clipId, string? app, string? title, long crc, CancellationToken ct = default);
    Task<(string? App, string? Title)?> GetSourceAsync(long clipId, CancellationToken ct = default);

    /// <summary>各来源程序的条目数（用于筛选和统计）。</summary>
    Task<List<(string App, int Count)>> GetSourceDistributionAsync(
        int top = 30, CancellationToken ct = default);
}

/// <summary>AI 请求（OpenAI 兼容的最小集合）。</summary>
public sealed record AiRequest(
    string SystemPrompt,
    string UserContent,
    int MaxTokens = 2000,
    float Temperature = 0.3f);

public sealed record AiResult(bool Success, string? Content, string? Error, int ElapsedMs);

/// <summary>AI 提供方。默认实现是 DeepSeek（OpenAI 兼容）。</summary>
public interface IAiProvider
{
    string Name { get; }
    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default);
    Task<(bool Ok, string Message, int ElapsedMs)> TestAsync(CancellationToken ct = default);

    /// <summary>拉取上游可用的模型列表（GET /models）。</summary>
    Task<(bool Ok, List<string> Models, string? Error)> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>凭据存储（Windows 凭据管理器）。</summary>
public interface ICredentialStore
{
    string? Read(string target);
    bool Write(string target, string secret);
    bool Delete(string target);
}

/// <summary>写前备份（Application 层通过它做编排，不直接依赖 Infrastructure）。</summary>
public interface IBackupService
{
    string BackupDirectory { get; }
    Task<string?> BackupDbAsync(string dbPath, bool force = false, CancellationToken ct = default);
    Task<string?> BackupSettingsAsync(string settingsPath, bool force = false, CancellationToken ct = default);

    /// <summary>列出数据库备份（最新在前）。</summary>
    IReadOnlyList<System.IO.FileInfo> ListBackups();

    /// <summary>删除一份备份。</summary>
    bool DeleteBackup(string fileName);

    /// <summary>只保留最新 N 份，返回删除数。</summary>
    int KeepLatest(int keep);

    /// <summary>备份总占用字节。</summary>
    long TotalSizeBytes();
}

/// <summary>外壳自身的配置。</summary>
/// <summary>侧栏里一个视图的配置。</summary>
public sealed class SidebarItemConfig
{
    /// <summary>视图标识（all/today/images/links/code/files/commands/longtext/contacts/secrets/uncategorized）。</summary>
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>一条搜索历史。</summary>
public sealed class RecentSearch
{
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string? Kind { get; set; }
    public string? From { get; set; }
    public int Hits { get; set; }
    public string At { get; set; } = "";
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public string? DittoPath { get; set; }
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "light";
    public int ListDensity { get; set; } = 70;

    /// <summary>侧栏视图的显隐与顺序。空 = 用默认。</summary>
    public List<SidebarItemConfig> SidebarItems { get; set; } = new();

    /// <summary>最近搜索记录（最多 10 条）。</summary>
    public List<RecentSearch> RecentSearches { get; set; } = new();

    /// <summary>结果少于这个数时自动用 AI 重排（0 = 关闭自动）。</summary>
    public int AiSearchThreshold { get; set; } = 5;

    /// <summary>每次 AI 搜索最多花多少元（0 = 不限制）。</summary>
    public double AiSearchMaxCost { get; set; } = 0.1;

    /// <summary>AI 会话上下文预算（tokens）。</summary>
    public int AiContextBudget { get; set; } = 524_288;

    /// <summary>压缩后保留多少 tokens 的原始对话。</summary>
    public int AiKeepAfterCompress { get; set; } = 16_000;

    /// <summary>AI 抽屉宽度（0 = 关闭）。</summary>
    public int AiDrawerWidth { get; set; } = 0;

    /// <summary>抽屉里的会话列表是否展开。</summary>
    public bool AiSessionListOpen { get; set; }

    /// <summary>AI 整理窗口的首次引导是否已看过。</summary>
    public bool OrganizeIntroSeen { get; set; }

    public string QuickPanelHotkey { get; set; } = "Ctrl+`";
    public string MainWindowHotkey { get; set; } = "Ctrl+Shift+`";
    public bool AiEnabled { get; set; }
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string AiModel { get; set; } = "deepseek-flash";
    public string AiKeyRef { get; set; } = "DittoShell/DeepSeek";
    public bool MaskSensitiveInList { get; set; } = true;
    public bool IsDemoMode { get; set; } = true;   // 骨架阶段：用假数据
}
