using System.Text.Json;

namespace NetZapret.Core.Services;

public sealed class SavedTargetCheck
{
    public string TargetName { get; set; } = "";
    public string EndpointText { get; set; } = "";
    public string Http11Status { get; set; } = "—";
    public string Tls12Status { get; set; } = "—";
    public string Tls13Status { get; set; } = "—";
    public string PingText { get; set; } = "—";
    public bool HasFailure { get; set; }
    public string? ErrorHint { get; set; }
}

public sealed class SavedStrategyBenchmark
{
    public string StrategyId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public int OkCount { get; set; }
    public int FailCount { get; set; }
    public int UnsupportedCount { get; set; }
    public int BlockedCount { get; set; }
    public int PingOkCount { get; set; }
    public double AvgPingMs { get; set; } = 9999;
    public bool IsBest { get; set; }
    public string? Error { get; set; }
    public List<SavedTargetCheck> Targets { get; set; } = [];
}

public sealed class SavedBenchmarkHistory
{
    public string Mode { get; set; } = "Standard";
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? BestStrategyId { get; set; }
    public List<SavedStrategyBenchmark> Entries { get; set; } = [];
}

public static class BenchmarkHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<SavedBenchmarkHistory?> LoadAsync(string assetsPath, CancellationToken ct = default)
    {
        var path = Path.Combine(assetsPath, "last-benchmark.json");
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SavedBenchmarkHistory>(stream, JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveAsync(string assetsPath, SavedBenchmarkHistory history, CancellationToken ct = default)
    {
        Directory.CreateDirectory(assetsPath);
        var path = Path.Combine(assetsPath, "last-benchmark.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, history, JsonOptions, ct);
    }
}