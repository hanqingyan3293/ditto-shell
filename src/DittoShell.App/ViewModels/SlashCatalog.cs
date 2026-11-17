namespace DittoShell.App.ViewModels;

/// <summary>
/// 斜杠命令的唯一来源。
/// 主界面和快速面板都从这里取，保证命令、顺序、行为完全一致。
/// </summary>
public static class SlashCatalog
{
    private static List<SlashItemVM>? _cache;

    public static IReadOnlyList<SlashItemVM> All
    {
        get
        {
            if (_cache is not null) return _cache;
            _cache = new List<SlashItemVM>
            {
                new("Tag",          "按标签筛选", "tag",    "选择标签",      true,  "B.Magenta"),
                new("FileText",     "按类型筛选", "type",   "图片/链接/代码", true,  "B.Primary"),
                new("ExternalLink", "按来源筛选", "from",   "Chrome/VSCode", true,  "B.Warning"),
                new("Calendar",     "按时间筛选", "age",    "今天/7天/30天",  true,  "B.Cyan"),
                new("Shield",       "只看凭据",   "secret", "敏感数据",      false, "B.Danger"),
                new("Wand",         "AI 搜索",    "ai",     "用自然语言找",  false, "B.Purple"),
                new("SearchX",      "清空筛选",   "clear",  "取消所有条件",  false, "B.Text3"),
            };
            return _cache;
        }
    }
}
