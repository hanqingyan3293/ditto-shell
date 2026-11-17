using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DittoShell.App.Localization;

/// <summary>
/// 极简本地化。XAML 用法：
///   Text="{Binding [quickPanel.searchPlaceholder], Source={x:Static loc:L.Instance}}"
/// 语言文件放在 exe 同级 Resources/ 下，用户可直接编辑。
/// </summary>
public sealed class L : INotifyPropertyChanged
{
    public static L Instance { get; } = new();

    private Dictionary<string, string> _map = new();

    public string Current { get; private set; } = "zh-CN";

    public string this[string key] =>
        _map.TryGetValue(key, out var v) ? v : $"[?{key}]";

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FilePath(string lang) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", $"Strings.{lang}.json");

    public void Load(string lang)
    {
        Current = lang;
        _map = TryRead(lang) ?? TryRead("zh-CN") ?? new Dictionary<string, string>();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }

    private static Dictionary<string, string>? TryRead(string lang)
    {
        try
        {
            var p = FilePath(lang);
            if (!File.Exists(p)) return null;
            var json = File.ReadAllText(p);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    public static string T(string key) => Instance[key];
}
