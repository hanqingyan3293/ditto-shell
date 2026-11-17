using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DittoShell.Application.Services;

namespace DittoShell.App.ViewModels;

public enum SettingKind { Text, Number, Bool, ReadOnly }

/// <summary>Ditto 原生设置里的一行。</summary>
public sealed partial class SettingRowVM : ObservableObject
{
    private readonly Func<SettingRowVM, Task>? _commit;
    private bool _loading;

    public SettingRowVM(string key, string label, string desc, SettingKind kind,
                        string rawValue, Func<SettingRowVM, Task>? commit = null)
    {
        Key = key;
        Label = label;
        Desc = desc;
        Kind = kind;
        _commit = commit;

        OriginalValue = rawValue ?? "0";
        _loading = true;
        _textValue = OriginalValue;
        _boolValue = OriginalValue is "1" or "true" or "True";
        _loading = false;
    }

    /// <summary>加载时的原始值。用于「没改就不写」，避免误触发写盘。</summary>
    public string OriginalValue { get; }

    public string Key { get; }
    public string Label { get; }
    public string Desc { get; }
    public SettingKind Kind { get; }
    public bool IsBool => Kind == SettingKind.Bool;
    public bool IsEditable => Kind != SettingKind.ReadOnly;

    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private bool _boolValue;

    public string RawValue => Kind == SettingKind.Bool ? (BoolValue ? "1" : "0") : TextValue;

    partial void OnTextValueChanged(string value)
    {
        if (_loading || Kind == SettingKind.ReadOnly) return;
        if (value == OriginalValue) return;          // 没变，别写
        _ = _commit?.Invoke(this);
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (_loading || Kind != SettingKind.Bool) return;
        var raw = value ? "1" : "0";
        if (raw == OriginalValue) return;
        _ = _commit?.Invoke(this);
    }
}

/// <summary>「Ditto 设置」分页的数据源：从 Ditto.Settings 读，改动即时写回。</summary>
public sealed partial class DittoSettingsVM : ObservableObject
{
    private readonly SettingsService? _service;

    public DittoSettingsVM(SettingsService? service = null)
    {
        _service = service;
        _ = LoadAsync();
    }

    public ObservableCollection<SettingRowVM> Rows { get; } = new();

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isAvailable = true;

    public async Task LoadAsync()
    {
        if (_service is null)
        {
            IsAvailable = false;
            Status = "未找到 Ditto.Settings，无法编辑原生设置";
            return;
        }

        IReadOnlyDictionary<string, string> all;
        try
        {
            all = await _service.ReadAllAsync();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Status = $"读取失败：{ex.Message}";
            return;
        }

        IsAvailable = true;
        Rows.Clear();

        void Add(string key, string label, string desc, SettingKind kind, string fallback = "0")
        {
            var v = all.TryGetValue(key, out var val) ? val : fallback;
            Rows.Add(new SettingRowVM(key, label, desc, kind, v, CommitAsync));
        }

        Add("MaxEntries", "最多保留条数", "超过后自动删除最旧的条目", SettingKind.Number, "10000");
        Add("CheckForExpiredEntries", "启用按天数清理", "开启后会删除超过保留天数的条目", SettingKind.Bool);
        Add("ExpiredEntries", "保留天数", "按天数清理启用时生效", SettingKind.Number, "5");
        Add("MaxClipSizeInBytes", "单条大小上限（字节）", "0 = 不限制（图片会占用大量空间）", SettingKind.Number, "0");
        Add("AllowDuplicates", "允许重复内容", "关闭则相同内容不会重复入库", SettingKind.Bool);
        Add("AllowBackToBackDuplicates", "连续两次相同也记录", "关闭则紧挨着的重复复制会被忽略", SettingKind.Bool);
        Add("SendPasteOnFirstTenHotKeys", "数字键直接粘贴", "快速面板里按 1-9 立刻粘贴", SettingKind.Bool, "1");
        Add("SimpleTextSearch", "简单文本搜索", "关闭后使用更复杂的匹配方式", SettingKind.Bool, "1");
        Add("TextOnlyPaste", "默认纯文本粘贴", "粘贴时去掉格式", SettingKind.Bool);
        Add("ShowIfClipWasPasted", "标记已粘贴过的条目", "列表里显示一个小标记", SettingKind.Bool);
        Add("ShowInTaskBar", "在任务栏显示 Ditto", "关闭后只在托盘显示", SettingKind.Bool);
        Add("ScaleImagesToDescWindow", "图片按窗口缩放", "预览大图时自适应窗口", SettingKind.Bool, "1");
        Add("CopyAppInclude", "只记录这些程序", "通配符，* 表示全部；多个用 ; 分隔", SettingKind.Text, "*");
        Add("CopyAppExclude", "排除这些程序", "这些程序复制的内容不会被记录", SettingKind.Text, "");

        // 只读展示
        if (all.TryGetValue("DittoHotKey", out var hk))
        {
            var code = long.TryParse(hk, out var n) ? n : 0;
            Rows.Add(new SettingRowVM("DittoHotKey", "Ditto 呼出快捷键",
                $"当前值 {code} → {Infrastructure.Settings.DittoSettingsGateway.DescribeHotkey(code)}",
                SettingKind.ReadOnly, hk));
        }

        Status = $"共 {Rows.Count} 项 · 改动即时写回 Ditto.Settings";
    }

    private async Task CommitAsync(SettingRowVM row)
    {
        if (_service is null || !IsAvailable) return;

        // 截图/无人值守模式下绝不写盘
        if (App.IsShotMode) { Status = "截图模式：已跳过写入"; return; }

        var raw = row.RawValue;

        // 数值类做基本校验，防止误输入写坏配置
        if (row.Kind == SettingKind.Number)
        {
            if (!long.TryParse(raw, out var n) || n < 0)
            {
                Status = $"{row.Label} 必须是非负整数";
                return;
            }
        }
        if (raw == row.OriginalValue) return;

        var r = await _service.SetAsync(row.Key, raw);
        Status = r.Success ? $"{row.Label} 已保存（部分项需重启 Ditto 生效）" : r.Message;
    }
}
