using CommunityToolkit.Mvvm.ComponentModel;

namespace DittoShell.App.ViewModels;

/// <summary>设置页「侧栏」分页里的一行。</summary>
public sealed partial class SidebarItemVM : ObservableObject
{
    public SidebarItemVM(string key, string name, bool visible, bool locked = false)
    {
        Key = key;
        Name = name;
        Visible = visible;
        IsLocked = locked;
    }

    public string Key { get; }
    public string Name { get; }
    public bool IsLocked { get; }
    public string LockHint => IsLocked ? "固定" : "";

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private int _order;
}
