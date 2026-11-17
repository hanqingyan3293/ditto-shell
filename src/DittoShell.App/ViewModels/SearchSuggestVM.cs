using CommunityToolkit.Mvvm.ComponentModel;

namespace DittoShell.App.ViewModels;

/// <summary>搜索框自动补全的一条候选。</summary>
public sealed partial class SearchSuggestVM : ObservableObject
{
    public SearchSuggestVM(string prefix, string value, string label, int count, string icon)
    {
        Prefix = prefix;
        Value = value;
        Label = label;
        Count = count;
        Icon = icon;
    }

    /// <summary>tag: / type: / from:</summary>
    public string Prefix { get; }

    /// <summary>补全的完整值（不含前缀）。</summary>
    public string Value { get; }

    /// <summary>显示用的名字。</summary>
    public string Label { get; }

    public int Count { get; }
    public string Icon { get; }
    public string CountText => Count > 0 ? $"{Count} 条" : "";

    /// <summary>完整输入形式（tag:工作）。</summary>
    public string Full => Prefix + Value;

    [ObservableProperty] private bool _isHighlighted;
}

/// <summary>一条搜索历史。</summary>
public sealed class RecentSearchVM
{
    public RecentSearchVM(string text, int hits)
    {
        Text = text;
        Hits = hits;
    }

    public string Text { get; }
    public int Hits { get; }
    public string HitsText => Hits > 0 ? $"{Hits} 条" : "";
}


/// <summary>快速面板筛选栏里的一个选项。</summary>
public sealed class FilterChoiceVM
{
    public FilterChoiceVM(string kind, string label, string value, int count)
    {
        Kind = kind;
        Label = label;
        Value = value;
        Count = count;
    }

    /// <summary>tag / type / from / age</summary>
    public string Kind { get; }
    public string Label { get; }
    public string Value { get; }
    public int Count { get; }
    public string CountText => Count > 0 ? $"{Count}" : "";
}
