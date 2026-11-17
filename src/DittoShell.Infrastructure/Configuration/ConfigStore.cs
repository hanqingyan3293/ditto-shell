using System.IO;
using System.Text.Json;
using DittoShell.Domain.Abstractions;

namespace DittoShell.Infrastructure.Configuration;

/// <summary>外壳自身配置（data/config.json）。与 Ditto 的设置互不影响。</summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ConfigStore(string? path = null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "config.json");
    }

    public string Path_ => _path;

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppConfig();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, Options);
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
