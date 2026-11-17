using DittoShell.Domain.Abstractions;

namespace DittoShell.Application.Services;

/// <summary>
/// Ditto 设置的读写编排：写前备份 → 校验 → 写入 → 返回可展示结果。
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsGateway _gateway;
    private readonly IBackupService? _backup;
    private readonly string? _settingsPath;

    public SettingsService(ISettingsGateway gateway, IBackupService? backup = null, string? settingsPath = null)
    {
        _gateway = gateway;
        _backup = backup;
        _settingsPath = settingsPath;
    }

    public Task<IReadOnlyDictionary<string, string>> ReadAllAsync(CancellationToken ct = default)
        => _gateway.ReadAllAsync(ct);

    public Task<long?> GetHotkeyAsync(CancellationToken ct = default)
        => _gateway.GetHotkeyAsync(ct);

    /// <summary>写一组设置。会先备份 Ditto.Settings（5 分钟内只备一次）。</summary>
    public async Task<OpResult> SaveAsync(IReadOnlyDictionary<string, string> changes,
                                          CancellationToken ct = default)
    {
        if (changes.Count == 0) return OpResult.Fail("没有需要保存的项");

        try
        {
            if (_backup is not null && _settingsPath is not null)
                await _backup.BackupSettingsAsync(_settingsPath, force: true, ct: ct);

            await _gateway.WriteAsync(changes, ct);

            return OpResult.Ok("已保存。部分设置需要重启 Ditto 才会生效。");
        }
        catch (UnauthorizedAccessException)
        {
            return OpResult.Fail("没有权限写入 Ditto.Settings");
        }
        catch (FileNotFoundException)
        {
            return OpResult.Fail("找不到 Ditto.Settings 文件");
        }
        catch (IOException ex)
        {
            return OpResult.Fail($"写入失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"保存失败：{ex.Message}");
        }
    }

    public async Task<OpResult> SetAsync(string key, string value, CancellationToken ct = default)
        => await SaveAsync(new Dictionary<string, string> { [key] = value }, ct);
}
