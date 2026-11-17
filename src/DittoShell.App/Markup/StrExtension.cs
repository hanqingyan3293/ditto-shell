using System.Windows.Data;
using System.Windows.Markup;
using DittoShell.App.Localization;

namespace DittoShell.App.Markup;

/// <summary>
/// 简写本地化绑定：Text="{loc:Str settings.general.language}"
/// 语言切换时自动刷新。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public StrExtension() { }
    public StrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = L.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>带格式参数的本地化：{loc:Str2 key, Arg=...}（暂只支持单参数静态值）。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Str2Extension : MarkupExtension
{
    public string Key { get; set; } = "";
    public string Arg { get; set; } = "";

    public Str2Extension() { }
    public Str2Extension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = L.Instance[Key];
        try { return string.Format(text, Arg); }
        catch { return text; }
    }
}
