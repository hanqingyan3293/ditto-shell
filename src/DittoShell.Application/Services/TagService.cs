using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>标签的用例编排：校验 + 调用存储 + 返回可展示结果。</summary>
public sealed class TagService
{
    private readonly ITagStore _store;

    public TagService(ITagStore store) => _store = store;

    public Task<IReadOnlyList<string>> GetTagsAsync(long clipId, CancellationToken ct = default)
        => _store.GetTagsAsync(clipId, ct);

    public Task<List<(string Name, int Count)>> GetAllAsync(CancellationToken ct = default)
        => _store.GetAllTagsAsync(ct);

    public async Task<OpResult> AddAsync(long clipId, string? tag, CancellationToken ct = default)
    {
        var name = (tag ?? "").Trim().TrimStart('#');
        if (name.Length == 0) return OpResult.Fail("标签不能为空");
        if (name.Length > 32) return OpResult.Fail("标签太长（最多 32 字）");
        if (name.Contains(' ')) return OpResult.Fail("标签不能包含空格");

        try
        {
            await _store.AddTagAsync(clipId, name, ct);
            return OpResult.Ok($"已添加 #{name}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"添加失败：{ex.Message}");
        }
    }

    public async Task<OpResult> RemoveAsync(long clipId, string? tag, CancellationToken ct = default)
    {
        var name = (tag ?? "").Trim().TrimStart('#');
        if (name.Length == 0) return OpResult.Fail("标签不能为空");

        try
        {
            await _store.RemoveTagAsync(clipId, name, ct);
            return OpResult.Ok($"已移除 #{name}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"移除失败：{ex.Message}");
        }
    }

    /// <summary>按标签查条目 ID（供搜索语法 tag:xxx 使用）。</summary>
    public async Task<IReadOnlyList<long>> FindClipIdsByTagAsync(string tag, CancellationToken ct = default)
    {
        // ITagStore 目前没有反向查询，这里用 GetAllAsync + 逐条比对成本太高，
        // 先留空实现，等搜索语法接通时补 SidecarStore 的查询方法。
        await Task.CompletedTask;
        return Array.Empty<long>();
    }
}
