using System.Diagnostics;
using System.IO;

namespace DittoShell.Infrastructure.DittoDb;

/// <summary>
/// 定位 Ditto 的可执行文件与数据库。
/// 顺序：配置 → 运行中的 Ditto 进程 → 常见安装路径 → 注册表 → 手动选择。
/// </summary>
public static class DittoLocator
{
    public sealed record Located(string DittoExe, string DatabasePath, string SettingsPath)
    {
        public bool DbExists => File.Exists(DatabasePath);
        public bool SettingsExists => File.Exists(SettingsPath);
        public string Directory => Path.GetDirectoryName(DatabasePath) ?? "";
    }

    /// <summary>尝试所有自动探测手段，失败返回 null。</summary>
    public static Located? Detect(string? hint = null)
    {
        // 1) 用户给定 / 上次记住的
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var fromHint = FromDirectory(hint);
            if (fromHint is not null) return fromHint;
        }

        // 2) 运行中的 Ditto 进程
        foreach (var p in SafeGetProcesses("Ditto"))
        {
            try
            {
                var exe = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) continue;
                var loc = FromDirectory(exe);
                if (loc is not null) return loc;
            }
            catch
            {
                // 权限不足时忽略，继续下一个手段
            }
        }

        // 3) 常见安装路径
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ditto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ditto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ditto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ditto"),
        };

        // 便携版常见位置：各盘根目录下的 Ditto / PortableGreen-APP\Ditto
        foreach (var drive in SafeDrives())
        {
            candidates.Add(Path.Combine(drive, "Ditto"));
            candidates.Add(Path.Combine(drive, "PortableGreen-APP", "Ditto"));
            candidates.Add(Path.Combine(drive, "Programs", "Ditto"));
        }

        foreach (var dir in candidates)
        {
            var loc = FromDirectory(dir);
            if (loc is not null) return loc;
        }

        // 4) 便携版多包一层（PortableGreen-APP\Ditto\DittoPortable_x_x_x\Ditto）
        foreach (var drive in SafeDrives())
        {
            var container = Path.Combine(drive, "PortableGreen-APP", "Ditto");
            if (!Directory.Exists(container)) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(container))
                {
                    var loc = FromDirectory(sub);
                    if (loc is not null) return loc;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>从一个目录（或 exe 路径）推出三个文件路径。</summary>
    public static Located? FromDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            if (!Directory.Exists(dir)) return null;

            // 若传入的是上层目录，向下找一层含 Ditto.db 的子目录
            var db = Path.Combine(dir, "Ditto.db");
            if (!File.Exists(db))
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var subDb = Path.Combine(sub, "Ditto.db");
                    if (File.Exists(subDb))
                    {
                        dir = sub;
                        db = subDb;
                        break;
                    }
                }
            }

            if (!File.Exists(db)) return null;

            var exe = Path.Combine(dir, "Ditto.exe");
            var settings = Path.Combine(dir, "Ditto.Settings");
            return new Located(exe, db, settings);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验一个数据库文件是否是我们认识的 Ditto 库（有 Main 表）。</summary>
    public static bool LooksLikeDittoDb(string dbPath, out string? error)
    {
        error = null;
        try
        {
            using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Main'";
            var r = cmd.ExecuteScalar();
            if (r is null)
            {
                error = "这个文件里没有 Ditto 的数据表（Main），可能不是 Ditto 数据库。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<Process> SafeGetProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    private static IEnumerable<string> SafeDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                    list.Add(d.RootDirectory.FullName);
            }
        }
        catch { }
        return list;
    }
}
