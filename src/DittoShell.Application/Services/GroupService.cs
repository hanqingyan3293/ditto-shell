using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

public sealed record OpResult(bool Success, string Message, long Id = 0)
{
    public static OpResult Ok(string msg = "", long id = 0) => new(true, msg, id);
    public static OpResult Fail(string msg) => new(false, msg);
}

/// <summary>
/// 分组操作的用例编排：校验 → 写入（内部含备份）→ 返回可展示的结果。
/// 不直接拼 SQL，全部经由 IClipWriter。
/// </summary>
public sealed class GroupService
{
    private readonly IClipWriter _writer;
    private readonly IClipReader _reader;

    public GroupService(IClipWriter writer, IClipReader reader)
    {
        _writer = writer;
        _reader = reader;
    }

    public async Task<OpResult> CreateAsync(string? name, long parentId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return OpResult.Fail("分组名不能为空");
        if (name.Length > 120) return OpResult.Fail("分组名太长（最多 120 字）");

        try
        {
            var id = await _writer.CreateGroupAsync(name, parentId, ct);
            return OpResult.Ok($"已创建「{name}」", id);
        }
        catch (Exception ex)
        {
            return OpResult.Fail(Describe(ex));
        }
    }

    public async Task<OpResult> RenameAsync(long groupId, string? newName, CancellationToken ct = default)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return OpResult.Fail("分组名不能为空");
        if (groupId <= 0) return OpResult.Fail("只能重命名分组");

        try
        {
            await _writer.RenameGroupAsync(groupId, newName, ct);
            return OpResult.Ok($"已重命名为「{newName}」");
        }
        catch (Exception ex)
        {
            return OpResult.Fail(Describe(ex));
        }
    }

    public async Task<OpResult> DeleteAsync(long groupId, CancellationToken ct = default)
    {
        if (groupId <= 0) return OpResult.Fail("只能删除分组");

        try
        {
            await _writer.DeleteGroupAsync(groupId, ct);
            return OpResult.Ok("分组已删除，条目已回到「未归类」");
        }
        catch (Exception ex)
        {
            return OpResult.Fail(Describe(ex));
        }
    }

    public async Task<OpResult> MoveClipsAsync(IReadOnlyList<long> clipIds, long groupId, CancellationToken ct = default)
    {
        if (clipIds.Count == 0) return OpResult.Fail("没有选中条目");

        try
        {
            await _writer.MoveToGroupAsync(clipIds, groupId, ct);
            return OpResult.Ok($"已移动 {clipIds.Count} 条");
        }
        catch (Exception ex)
        {
            return OpResult.Fail(Describe(ex));
        }
    }

    public async Task<OpResult> ReparentAsync(long groupId, long newParentId, CancellationToken ct = default)
    {
        if (groupId <= 0) return OpResult.Fail("只能移动分组");
        if (groupId == newParentId) return OpResult.Fail("不能移到自己下面");

        try
        {
            await _writer.ReparentGroupAsync(groupId, newParentId, ct);
            return OpResult.Ok("已移动分组");
        }
        catch (Exception ex)
        {
            return OpResult.Fail(Describe(ex));
        }
    }

    /// <summary>把底层异常翻译成用户能看懂的中文。</summary>
    private static string Describe(Exception ex)
    {
        var msg = ex.Message;

        if (msg.Contains("SQLITE_BUSY") || msg.Contains("被占用") || msg.Contains("database is locked"))
            return "Ditto 正在写数据库，请稍等一两秒再试";

        if (msg.Contains("readonly") || msg.Contains("read-only"))
            return "数据库是只读的，无法修改";

        if (ex is UnauthorizedAccessException)
            return "没有权限写入数据库文件";

        if (ex is IOException)
            return "磁盘读写出错（可能空间不足或被占用）";

        return $"操作失败：{msg}";
    }
}
