namespace DittoShell.Domain.Models;

/// <summary>分组树节点。Ditto 的 lParentID 天然支持任意层级。</summary>
public sealed class GroupNode
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long ParentId { get; init; } = -1;
    public int Count { get; set; }
    public List<GroupNode> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;

    public static GroupNode Virtual(long id, string name, int count = 0) =>
        new() { Id = id, Name = name, Count = count };
}
