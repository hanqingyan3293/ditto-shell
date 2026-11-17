using CommunityToolkit.Mvvm.ComponentModel;

namespace DittoShell.App.ViewModels;

/// <summary>左侧标签筛选的一个小胶囊。</summary>
public sealed partial class TagChipVM : ObservableObject
{
    public TagChipVM(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }
    public int Count { get; }
    public string Display => $"#{Name}";
    public string CountText => Count.ToString();

    [ObservableProperty] private bool _isSelected;
}
