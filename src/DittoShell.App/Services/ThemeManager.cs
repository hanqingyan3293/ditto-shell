using System.Windows;

namespace DittoShell.App.Services;

/// <summary>运行时切换主题：只替换合并字典的第 0 项，布局不动。</summary>
public static class ThemeManager
{
    public static string Current { get; private set; } = "light";

    public static void Apply(string theme)
    {
        theme = theme?.ToLowerInvariant() == "dark" ? "dark" : "light";
        Current = theme;

        var source = new Uri($"Assets/Tokens.{(theme == "dark" ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = source };

        var app = System.Windows.Application.Current;
        if (app is null) return;

        if (app.Resources.MergedDictionaries.Count > 0)
            app.Resources.MergedDictionaries[0] = dict;
        else
            app.Resources.MergedDictionaries.Add(dict);
    }

    public static void Toggle() => Apply(Current == "dark" ? "light" : "dark");
}
