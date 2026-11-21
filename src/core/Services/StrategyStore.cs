using System.Text.Json;
using System.Text.Json.Serialization;
using NetZapret.Core.Models;

namespace NetZapret.Core.Services;

public sealed class AppSettings
{
    [JsonPropertyName("selectedStrategyId")]
    public string SelectedStrategyId { get; set; } = "multisplit-4pda";

    [JsonPropertyName("gameFilter")]
    public GameFilterMode GameFilter { get; set; } = GameFilterMode.Disabled;

    [JsonPropertyName("gameTcpPorts")]
    public string GameTcpPorts { get; set; } = "1024-65535";

    [JsonPropertyName("gameUdpPorts")]
    public string GameUdpPorts { get; set; } = "1024-65535";

    [JsonPropertyName("autoCheckUpdates")]
    public bool AutoCheckUpdates { get; set; } = true;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("useServiceMode")]
    public bool UseServiceMode { get; set; }

    [JsonPropertyName("isSidebarCollapsed")]
    public bool IsSidebarCollapsed { get; set; }
}

public static class StrategyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<StrategyManifest> LoadManifestAsync(string assetsPath, CancellationToken ct = default)
    {
        var path = Path.Combine(assetsPath, "strategies.json");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<StrategyManifest>(stream, JsonOptions, ct)
                       ?? new StrategyManifest();

        var custom = await LoadCustomStrategiesAsync(assetsPath, ct);
        foreach (var item in custom)
        {
            item.IsCustom = true;
            var existingIdx = manifest.Strategies.FindIndex(s => string.Equals(s.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
                manifest.Strategies[existingIdx] = item;
            else
                manifest.Strategies.Add(item);
        }

        return manifest;
    }

    public static async Task<List<StrategyDefinition>> LoadCustomStrategiesAsync(string assetsPath, CancellationToken ct = default)
    {
        var customPath = Path.Combine(assetsPath, "custom-strategies.json");
        if (!File.Exists(customPath))
            return [];

        try
        {
            await using var stream = File.OpenRead(customPath);
            return await JsonSerializer.DeserializeAsync<List<StrategyDefinition>>(stream, JsonOptions, ct) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static async Task SaveCustomStrategyAsync(string assetsPath, StrategyDefinition strategy, CancellationToken ct = default)
    {
        strategy.IsCustom = true;
        var list = await LoadCustomStrategiesAsync(assetsPath, ct);
        var idx = list.FindIndex(s => string.Equals(s.Id, strategy.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            list[idx] = strategy;
        else
            list.Add(strategy);

        await WriteCustomStrategiesAsync(assetsPath, list, ct);
    }

    public static async Task DeleteCustomStrategyAsync(string assetsPath, string strategyId, CancellationToken ct = default)
    {
        var list = await LoadCustomStrategiesAsync(assetsPath, ct);
        list.RemoveAll(s => string.Equals(s.Id, strategyId, StringComparison.OrdinalIgnoreCase));
        await WriteCustomStrategiesAsync(assetsPath, list, ct);
    }

    private static async Task WriteCustomStrategiesAsync(string assetsPath, List<StrategyDefinition> list, CancellationToken ct)
    {
        Directory.CreateDirectory(assetsPath);
        var customPath = Path.Combine(assetsPath, "custom-strategies.json");
        await using var stream = File.Create(customPath);
        await JsonSerializer.SerializeAsync(stream, list, JsonOptions, ct);
    }

    public static async Task<AppSettings> LoadSettingsAsync(string assetsPath, CancellationToken ct = default)
    {
        var path = Path.Combine(assetsPath, "settings.json");
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static async Task SaveSettingsAsync(string assetsPath, AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(assetsPath);
        var path = Path.Combine(assetsPath, "settings.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);
    }

    public static StrategyDefinition? FindStrategy(StrategyManifest manifest, string id) =>
        manifest.Strategies.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? manifest.Strategies.FirstOrDefault();
}