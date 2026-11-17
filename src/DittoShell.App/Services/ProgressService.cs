using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DittoShell.App.Services;

public sealed class ProgressItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("done")] public bool Done { get; set; }
}

public sealed class ProgressBatch
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("items")] public List<ProgressItem> Items { get; set; } = new();

    public int Total => Items.Count;
    public int Done => Items.Count(i => i.Done);
    public bool AllDone => Total > 0 && Done == Total;
    public bool Started => Done > 0;
    public double Percent => Total == 0 ? 0 : (double)Done / Total;
}

public sealed class ProgressDoc
{
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("batches")] public List<ProgressBatch> Batches { get; set; } = new();
}

/// <summary>读取 Resources/progress.json，供「开发进度」页展示。</summary>
public static class ProgressService
{
    private static ProgressDoc? _cached;

    public static ProgressDoc Load()
    {
        if (_cached is not null) return _cached;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "progress.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _cached = JsonSerializer.Deserialize<ProgressDoc>(json) ?? new ProgressDoc();
            }
            else
            {
                _cached = new ProgressDoc();
            }
        }
        catch
        {
            _cached = new ProgressDoc();
        }
        return _cached;
    }

    public static int TotalItems => Load().Batches.Sum(b => b.Total);
    public static int DoneItems => Load().Batches.Sum(b => b.Done);
    public static double OverallPercent => TotalItems == 0 ? 0 : (double)DoneItems / TotalItems;
}
