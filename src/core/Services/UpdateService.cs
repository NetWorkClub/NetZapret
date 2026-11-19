using System.Net.Http.Headers;

namespace NetZapret.Core.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleasePageUrl,
    string? Error = null
);

public static class UpdateService
{
    public const string AppVersion = "1.0.1";
    public const string DefaultLocalVersion = "1.10.4";
    public const string ReleasesLatestUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/latest";
    public const string ReleaseTagPrefix = "https://github.com/Flowseal/zapret-discord-youtube/releases/tag/";

    public static string GetLocalVersion(string assetsPath)
    {
        var versionFile = Path.Combine(assetsPath, "version.txt");
        if (File.Exists(versionFile))
        {
            var raw = File.ReadAllText(versionFile).Trim();
            if (!string.IsNullOrEmpty(raw))
                return raw;
        }

        return DefaultLocalVersion;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string assetsPath,
        CancellationToken ct = default)
    {
        var current = GetLocalVersion(assetsPath);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            var url = $"{ListManager.VersionRepoUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var latest = (await client.GetStringAsync(url, ct)).Trim();

            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateCheckResult(false, false, current, current, ReleasesLatestUrl, "Пустой ответ от сервера.");

            var available = IsNewer(latest, current);
            var releaseUrl = available ? $"{ReleaseTagPrefix}{latest}" : ReleasesLatestUrl;

            return new UpdateCheckResult(true, available, current, latest, releaseUrl);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, current, current, ReleasesLatestUrl, ex.Message);
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var vLatest) && Version.TryParse(current, out var vCurrent))
            return vLatest > vCurrent;

        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }
}