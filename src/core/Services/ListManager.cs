using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NetZapret.Core.Models;

namespace NetZapret.Core.Services;

public sealed record FakeBinInfo(string Name, string FullPath, string Sha256);

public sealed record ActiveFakesState(
    string DiscordFakeName,
    string GameFakeName,
    IReadOnlyList<FakeBinInfo> AvailableFakes
);

public static partial class ListManager
{
    public const string IpsetRepoUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";

    public const string HostsRepoUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";

    public const string VersionRepoUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/version.txt";

    private const string ListsBaseRepoUrl =
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/lists/";

    private static readonly string[] BaseListFiles =
    [
        "list-general.txt",
        "list-google.txt",
        "list-exclude.txt",
        "ipset-exclude.txt"
    ];

    private const string HostsStartMarker = "# NetZapret START";
    private const string HostsEndMarker = "# NetZapret END";

    [GeneratedRegex(@"^([1-9]\d*)(-[1-9]\d*)?$")]
    private static partial Regex PortItemRegex();

    [GeneratedRegex(@"# NetZapret START[\s\S]*?# NetZapret END\r?\n?")]
    private static partial Regex HostsBlockRegex();

    public static void EnsureUserListsExist(string assetsPath)
    {
        var listsDir = Path.Combine(assetsPath, "lists");
        Directory.CreateDirectory(listsDir);

        var ipsetExcludeUser = Path.Combine(listsDir, "ipset-exclude-user.txt");
        if (!File.Exists(ipsetExcludeUser))
            File.WriteAllText(ipsetExcludeUser, "203.0.113.113/32" + Environment.NewLine, Encoding.UTF8);

        var listGeneralUser = Path.Combine(listsDir, "list-general-user.txt");
        if (!File.Exists(listGeneralUser))
            File.WriteAllText(listGeneralUser, "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine, Encoding.UTF8);

        var listExcludeUser = Path.Combine(listsDir, "list-exclude-user.txt");
        if (!File.Exists(listExcludeUser))
            File.WriteAllText(listExcludeUser, "domain.example.abc" + Environment.NewLine, Encoding.UTF8);
    }

    public static IpsetMode GetIpsetMode(string assetsPath)
    {
        var listFile = Path.Combine(assetsPath, "lists", "ipset-all.txt");
        if (!File.Exists(listFile))
            return IpsetMode.Any;

        var raw = File.ReadAllText(listFile);
        if (string.IsNullOrWhiteSpace(raw))
            return IpsetMode.Any;

        if (raw.Contains("203.0.113.113/32", StringComparison.Ordinal))
            return IpsetMode.None;

        return IpsetMode.Loaded;
    }

    public static bool SetIpsetMode(string assetsPath, IpsetMode targetMode)
    {
        var listsDir = Path.Combine(assetsPath, "lists");
        Directory.CreateDirectory(listsDir);

        var listFile = Path.Combine(listsDir, "ipset-all.txt");
        var backupFile = Path.Combine(listsDir, "ipset-all.txt.backup");
        var current = GetIpsetMode(assetsPath);

        if (current == targetMode)
            return true;

        if (current == IpsetMode.Loaded && File.Exists(listFile))
            File.Copy(listFile, backupFile, overwrite: true);

        switch (targetMode)
        {
            case IpsetMode.None:
                File.WriteAllText(listFile, "203.0.113.113/32" + Environment.NewLine, Encoding.UTF8);
                return true;

            case IpsetMode.Any:
                File.WriteAllText(listFile, string.Empty, Encoding.UTF8);
                return true;

            case IpsetMode.Loaded:
                if (!File.Exists(backupFile))
                    return false;
                File.Copy(backupFile, listFile, overwrite: true);
                return true;

            default:
                return false;
        }
    }

    public static async Task UpdateIpsetFromRepoAsync(string assetsPath, CancellationToken ct = default)
    {
        var listsDir = Path.Combine(assetsPath, "lists");
        Directory.CreateDirectory(listsDir);

        var listFile = Path.Combine(listsDir, "ipset-all.txt");
        var backupFile = Path.Combine(listsDir, "ipset-all.txt.backup");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = await client.GetStringAsync(IpsetRepoUrl, ct);

        await File.WriteAllTextAsync(listFile, content, Encoding.UTF8, ct);
        await File.WriteAllTextAsync(backupFile, content, Encoding.UTF8, ct);
    }

    public static async Task<string?> UpdateAllListsFromRepoAsync(string assetsPath, CancellationToken ct = default)
    {
        var listsDir = Path.Combine(assetsPath, "lists");
        Directory.CreateDirectory(listsDir);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        foreach (var fileName in BaseListFiles)
        {
            var url = $"{ListsBaseRepoUrl}{fileName}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var content = await client.GetStringAsync(url, ct);
            if (!string.IsNullOrWhiteSpace(content))
                await File.WriteAllTextAsync(Path.Combine(listsDir, fileName), content, Encoding.UTF8, ct);
        }

        var ipsetModeBefore = GetIpsetMode(assetsPath);
        var ipsetContent = await client.GetStringAsync(IpsetRepoUrl, ct);
        await File.WriteAllTextAsync(Path.Combine(listsDir, "ipset-all.txt.backup"), ipsetContent, Encoding.UTF8, ct);
        if (ipsetModeBefore == IpsetMode.Loaded)
            await File.WriteAllTextAsync(Path.Combine(listsDir, "ipset-all.txt"), ipsetContent, Encoding.UTF8, ct);

        string? remoteVersion = null;
        try
        {
            var verUrl = $"{VersionRepoUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            remoteVersion = (await client.GetStringAsync(verUrl, ct)).Trim();
            if (!string.IsNullOrWhiteSpace(remoteVersion))
                await File.WriteAllTextAsync(Path.Combine(assetsPath, "version.txt"), remoteVersion, Encoding.UTF8, ct);
        }
        catch
        {
        }

        return remoteVersion;
    }

    public static async Task<string> FetchRemoteHostsAsync(CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var url = $"{HostsRepoUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return await client.GetStringAsync(url, ct);
    }

    public static bool IsHostsUpdateNeeded(string currentHostsContent, string remoteHostsContent)
    {
        var normalizedRemote = NormalizeLines(remoteHostsContent);
        if (normalizedRemote.Length == 0)
            return false;

        var existingLines = currentHostsContent
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return normalizedRemote.Any(line => !existingLines.Contains(line));
    }

    public static string MergeHostsContent(string currentHostsContent, string remoteHostsContent)
    {
        var cleaned = HostsBlockRegex().Replace(currentHostsContent, string.Empty).TrimEnd();
        var remoteLines = NormalizeLines(remoteHostsContent);

        var sb = new StringBuilder();
        if (cleaned.Length > 0)
        {
            sb.AppendLine(cleaned);
            sb.AppendLine();
        }

        sb.AppendLine(HostsStartMarker);
        foreach (var line in remoteLines)
            sb.AppendLine(line);
        sb.AppendLine(HostsEndMarker);

        return sb.ToString();
    }

    public static async Task<ActiveFakesState> GetActiveFakesAsync(string assetsPath, CancellationToken ct = default)
    {
        var fakesDir = Path.Combine(assetsPath, "bin", "fakes");
        Directory.CreateDirectory(fakesDir);

        var allBins = Directory.GetFiles(fakesDir, "*.bin")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var available = new List<FakeBinInfo>();
        string? discordHash = null;
        string? gameHash = null;

        foreach (var path in allBins)
        {
            var fileName = Path.GetFileName(path);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            if (string.Equals(fileName, "ACTIVE_DISCORD_UDP.bin", StringComparison.OrdinalIgnoreCase))
                discordHash = hash;
            else if (string.Equals(fileName, "ACTIVE_GAME_UDP.bin", StringComparison.OrdinalIgnoreCase))
                gameHash = hash;
            else if (!fileName.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
                available.Add(new FakeBinInfo(Path.GetFileNameWithoutExtension(fileName), path, hash));
        }

        var discordName = available.FirstOrDefault(x => x.Sha256 == discordHash)?.Name ?? "custom / not found";
        var gameName = available.FirstOrDefault(x => x.Sha256 == gameHash)?.Name ?? "custom / not found";

        return new ActiveFakesState(discordName, gameName, available);
    }

    public static void ReplaceActiveFake(string assetsPath, bool isDiscord, string sourceFakePath)
    {
        var fakesDir = Path.Combine(assetsPath, "bin", "fakes");
        var targetName = isDiscord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin";
        var targetPath = Path.Combine(fakesDir, targetName);
        File.Copy(sourceFakePath, targetPath, overwrite: true);
    }

    public static bool ValidatePortRanges(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var compact = raw.Replace(" ", string.Empty, StringComparison.Ordinal);
        var parts = compact.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            var match = PortItemRegex().Match(part);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups[1].Value, out var start) || start < 1 || start > 65535)
                return false;

            if (match.Groups[2].Success)
            {
                var endRaw = match.Groups[2].Value[1..];
                if (!int.TryParse(endRaw, out var end) || end < start || end > 65535)
                    return false;
            }
        }

        normalized = compact;
        return true;
    }

    private static string[] NormalizeLines(string content) =>
        content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();
}