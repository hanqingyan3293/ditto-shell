using System.IO;
using System.IO.Compression;
using System.Text;
using DittoShell.Domain.Models;

namespace DittoShell.Application.Services;

/// <summary>导出与迁移包。</summary>
public sealed class ExportService
{
    /// <summary>把选中的条目导出成纯文本（每条之间用分隔线）。</summary>
    public async Task<OpResult> ExportClipsAsTextAsync(
        IReadOnlyList<(ClipItem Item, string Text)> clips, string path, CancellationToken ct = default)
    {
        if (clips.Count == 0) return OpResult.Fail("没有可导出的条目");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Ditto Shell 导出 · {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"# 共 {clips.Count} 条");
            sb.AppendLine();

            foreach (var (item, text) in clips)
            {
                sb.AppendLine(new string('─', 40));
                sb.AppendLine($"# {item.Id} · {item.CreatedAt:yyyy-MM-dd HH:mm} · {item.Kind}");
                sb.AppendLine(text);
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
            return OpResult.Ok($"已导出 {clips.Count} 条到 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"导出失败：{ex.Message}");
        }
    }

    /// <summary>把选中的条目导出成 JSON（便于程序处理）。</summary>
    public async Task<OpResult> ExportClipsAsJsonAsync(
        IReadOnlyList<(ClipItem Item, string Text)> clips, string path, CancellationToken ct = default)
    {
        if (clips.Count == 0) return OpResult.Fail("没有可导出的条目");

        try
        {
            var payload = clips.Select(c => new
            {
                id = c.Item.Id,
                created = c.Item.CreatedAt.ToString("o"),
                kind = c.Item.Kind.ToString(),
                groupId = c.Item.GroupId,
                text = c.Text
            });

            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
            return OpResult.Ok($"已导出 {clips.Count} 条到 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 换机迁移包：Ditto.db + data 目录（不含日志）打成一个 zip。
    /// 解压到新机器后，把 db 放到 Ditto 目录、data 放到程序目录即可。
    /// </summary>
    public async Task<OpResult> CreateMigrationPackageAsync(
        string zipPath, string dittoDbPath, string dataDir, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(dittoDbPath)) return OpResult.Fail("找不到 Ditto.db");

            if (File.Exists(zipPath)) File.Delete(zipPath);

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

                zip.CreateEntryFromFile(dittoDbPath, "Ditto.db", CompressionLevel.Optimal);

                if (Directory.Exists(dataDir))
                {
                    foreach (var f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(dataDir, f);
                        if (rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase)) continue;
                        if (rel.EndsWith("-wal") || rel.EndsWith("-shm")) continue;

                        zip.CreateEntryFromFile(f, "data/" + rel.Replace('\\', '/'), CompressionLevel.Optimal);
                    }
                }

                var readme = zip.CreateEntry("README.txt", CompressionLevel.NoCompression);
                using var w = new StreamWriter(readme.Open(), Encoding.UTF8);
                w.WriteLine("Ditto Shell 迁移包");
                w.WriteLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
                w.WriteLine();
                w.WriteLine("使用方法：");
                w.WriteLine("1. 关闭 Ditto 和 Ditto Shell");
                w.WriteLine("2. 把 Ditto.db 复制到 Ditto 安装目录（覆盖原文件）");
                w.WriteLine("3. 把 data 目录复制到 Ditto Shell 程序目录");
                w.WriteLine("4. 启动 Ditto，再启动 Ditto Shell");
            }, ct);

            var size = new FileInfo(zipPath).Length / 1024.0 / 1024;
            return OpResult.Ok($"迁移包已生成：{Path.GetFileName(zipPath)}（{size:F1} MB）");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"打包失败：{ex.Message}");
        }
    }

    /// <summary>清理过期的自动备份，返回删除数量。</summary>
    public int CleanupBackups(string backupDir, int keep)
    {
        if (!Directory.Exists(backupDir)) return 0;

        try
        {
            var files = new DirectoryInfo(backupDir)
                .GetFiles("Ditto_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(keep)
                .ToList();

            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
            return files.Count;
        }
        catch
        {
            return 0;
        }
    }
}
