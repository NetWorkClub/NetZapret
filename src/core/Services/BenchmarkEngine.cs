using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NetZapret.Core.Services;

public enum CheckStatus
{
    Ok,
    Fail,
    Unsupported,
    SslMismatch,
    LikelyBlocked
}

public enum ProbeType
{
    Http11,
    Tls12,
    Tls13
}

public sealed record TargetItem(string Name, string? Url, string PingHost);

public sealed record ProbeResult(ProbeType Probe, CheckStatus Status, double ElapsedMs, string? Detail = null);

public sealed record TargetCheckResult(
    TargetItem Target,
    IReadOnlyList<ProbeResult> Probes,
    long? PingMs
);

public sealed class DpiSuiteEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";
}

public sealed record DpiCheckResult(
    DpiSuiteEntry Target,
    IReadOnlyList<ProbeResult> Probes,
    bool HasFreezePattern
);

public sealed record StrategyBenchmarkSummary(
    string StrategyId,
    string StrategyName,
    int OkCount,
    int FailCount,
    int UnsupportedCount,
    int BlockedCount,
    int PingOkCount,
    double AveragePingMs
);

public static partial class BenchmarkEngine
{
    public const string DpiSuiteUrl =
        "https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json";

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*""(.+)""\s*$")]
    private static partial Regex TargetLineRegex();

    public static string GetEffectiveTargetsPath(string assetsPath)
    {
        var utilsPath = Path.Combine(assetsPath, "utils", "targets.txt");
        if (File.Exists(utilsPath))
            return utilsPath;

        return Path.Combine(assetsPath, "targets.txt");
    }

    public static IReadOnlyList<TargetItem> LoadTargets(string targetsFilePath)
    {
        var items = new List<TargetItem>();

        if (File.Exists(targetsFilePath))
        {
            foreach (var line in File.ReadAllLines(targetsFilePath))
            {
                var match = TargetLineRegex().Match(line);
                if (!match.Success)
                    continue;

                var name = match.Groups[1].Value;
                var val = match.Groups[2].Value.Trim();

                if (val.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = val["PING:".Length..].Trim();
                    items.Add(new TargetItem(name, null, host));
                }
                else
                {
                    var host = Regex.Replace(val, @"^https?://", string.Empty, RegexOptions.IgnoreCase);
                    var slash = host.IndexOf('/');
                    if (slash >= 0) host = host[..slash];
                    items.Add(new TargetItem(name, val, host));
                }
            }
        }

        if (items.Count > 0)
            return items;

        return
        [
            new TargetItem("DiscordMain", "https://discord.com", "discord.com"),
            new TargetItem("DiscordGateway", "https://gateway.discord.gg", "gateway.discord.gg"),
            new TargetItem("DiscordCDN", "https://cdn.discordapp.com", "cdn.discordapp.com"),
            new TargetItem("YouTubeWeb", "https://www.youtube.com", "www.youtube.com"),
            new TargetItem("YouTubeVideoRedirect", "https://redirector.googlevideo.com", "redirector.googlevideo.com"),
            new TargetItem("GoogleMain", "https://www.google.com", "www.google.com"),
            new TargetItem("CloudflareWeb", "https://www.cloudflare.com", "www.cloudflare.com"),
            new TargetItem("CloudflareDNS1111", null, "1.1.1.1"),
            new TargetItem("GoogleDNS8888", null, "8.8.8.8")
        ];
    }

    public static void SaveTargets(string targetsFilePath, IEnumerable<TargetItem> targets)
    {
        var dir = Path.GetDirectoryName(targetsFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var t in targets)
        {
            var cleanKey = Regex.Replace(t.Name.Trim(), @"[^\w]", "_");
            if (string.IsNullOrWhiteSpace(cleanKey))
                cleanKey = "Target";

            var val = string.IsNullOrWhiteSpace(t.Url)
                ? $"PING:{t.PingHost.Trim()}"
                : t.Url.Trim();

            sb.AppendLine($"{cleanKey} = \"{val}\"");
        }

        File.WriteAllText(targetsFilePath, sb.ToString(), Encoding.UTF8);
    }

    public static IReadOnlyList<TargetItem> SelectQuickTargets(IReadOnlyList<TargetItem> allTargets)
    {
        var urlTargets = allTargets.Where(t => t.Url is not null).ToList();
        var priorityNames = new[] { "DiscordMain", "YouTubeWeb", "YouTubeVideoRedirect" };
        var picked = new List<TargetItem>();

        foreach (var p in priorityNames)
        {
            var found = urlTargets.FirstOrDefault(t => string.Equals(t.Name, p, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                picked.Add(found);
        }

        foreach (var t in urlTargets)
        {
            if (picked.Count >= 3)
                break;
            if (!picked.Contains(t))
                picked.Add(t);
        }

        return picked;
    }

    public static async Task<IReadOnlyList<TargetCheckResult>> RunStandardAsync(
        IReadOnlyList<TargetItem> targets,
        TimeSpan timeout,
        int maxParallelTargets = 12,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(maxParallelTargets);
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var pingTask = PingAsync(target.PingHost, 1000);
                Task<ProbeResult[]> probesTask = target.Url is not null
                    ? Task.WhenAll(
                        ProbeUrlAsync(target.Url, ProbeType.Http11, timeout, ct),
                        ProbeUrlAsync(target.Url, ProbeType.Tls12, timeout, ct),
                        ProbeUrlAsync(target.Url, ProbeType.Tls13, timeout, ct))
                    : Task.FromResult(Array.Empty<ProbeResult>());

                await Task.WhenAll(pingTask, probesTask);
                return new TargetCheckResult(target, probesTask.Result, pingTask.Result);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public static async Task<IReadOnlyList<DpiSuiteEntry>> FetchDpiSuiteAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            var list = await client.GetFromJsonAsync<List<DpiSuiteEntry>>(DpiSuiteUrl, ct);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<DpiCheckResult>> RunDpiSuiteAsync(
        IReadOnlyList<DpiSuiteEntry> targets,
        TimeSpan timeout,
        int payloadBytes = 65536,
        int maxParallelTargets = 8,
        CancellationToken ct = default)
    {
        var payload = RandomNumberGenerator.GetBytes(payloadBytes);
        using var gate = new SemaphoreSlim(maxParallelTargets);

        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var probes = await Task.WhenAll(
                    ProbeDpiPayloadAsync(target.Host, ProbeType.Http11, timeout, payload, ct),
                    ProbeDpiPayloadAsync(target.Host, ProbeType.Tls12, timeout, payload, ct),
                    ProbeDpiPayloadAsync(target.Host, ProbeType.Tls13, timeout, payload, ct));

                var freeze = probes.Any(p => p.Status == CheckStatus.LikelyBlocked);
                return new DpiCheckResult(target, probes, freeze);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public static StrategyBenchmarkSummary SummarizeStandard(
        string strategyId,
        string strategyName,
        IReadOnlyList<TargetCheckResult> results)
    {
        var ok = 0;
        var fail = 0;
        var unsup = 0;
        var pingOk = 0;
        var pingSum = 0L;

        foreach (var r in results)
        {
            foreach (var p in r.Probes)
            {
                switch (p.Status)
                {
                    case CheckStatus.Ok:
                        ok++;
                        break;
                    case CheckStatus.Unsupported:
                        unsup++;
                        break;
                    default:
                        fail++;
                        break;
                }
            }

            if (r.PingMs is { } ms)
            {
                pingOk++;
                pingSum += ms;
            }
        }

        var avgPing = pingOk > 0 ? (double)pingSum / pingOk : 9999.0;
        return new StrategyBenchmarkSummary(strategyId, strategyName, ok, fail, unsup, 0, pingOk, avgPing);
    }

    public static StrategyBenchmarkSummary SummarizeDpi(
        string strategyId,
        string strategyName,
        IReadOnlyList<DpiCheckResult> results)
    {
        var ok = 0;
        var fail = 0;
        var unsup = 0;
        var blocked = 0;

        foreach (var r in results)
        {
            foreach (var p in r.Probes)
            {
                switch (p.Status)
                {
                    case CheckStatus.Ok:
                        ok++;
                        break;
                    case CheckStatus.Unsupported:
                        unsup++;
                        break;
                    case CheckStatus.LikelyBlocked:
                        blocked++;
                        break;
                    default:
                        fail++;
                        break;
                }
            }
        }

        return new StrategyBenchmarkSummary(strategyId, strategyName, ok, fail, unsup, blocked, 0, 0);
    }

    public static async Task<ProbeResult> ProbeUrlAsync(
        string url,
        ProbeType probe,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var handler = CreateFreshHandler(probe, timeout);
        using var client = new HttpClient(handler) { Timeout = timeout };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            if (probe == ProbeType.Http11)
            {
                req.Version = HttpVersion.Version11;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ProbeResult(probe, CheckStatus.Ok, sw.Elapsed.TotalMilliseconds, $"{(int)resp.StatusCode}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException authEx)
        {
            var msg = authEx.Message;
            if (msg.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("protocol", StringComparison.OrdinalIgnoreCase))
                return new ProbeResult(probe, CheckStatus.Unsupported, sw.Elapsed.TotalMilliseconds, msg);

            return new ProbeResult(probe, CheckStatus.SslMismatch, sw.Elapsed.TotalMilliseconds, msg);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            return new ProbeResult(probe, CheckStatus.Unsupported, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            return new ProbeResult(probe, CheckStatus.Fail, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    private static async Task<ProbeResult> ProbeDpiPayloadAsync(
        string host,
        ProbeType probe,
        TimeSpan timeout,
        byte[] payload,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var handler = CreateFreshHandler(probe, timeout);
        using var client = new HttpClient(handler) { Timeout = timeout };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}")
            {
                Content = new ByteArrayContent(payload)
            };
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, payload.Length - 1);

            if (probe == ProbeType.Http11)
            {
                req.Version = HttpVersion.Version11;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ProbeResult(probe, CheckStatus.Ok, sw.Elapsed.TotalMilliseconds, $"{(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            var status = sw.Elapsed >= timeout * 0.9 ? CheckStatus.LikelyBlocked : CheckStatus.Fail;
            return new ProbeResult(probe, status, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return new ProbeResult(probe, CheckStatus.Unsupported, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            return new ProbeResult(probe, CheckStatus.Unsupported, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            return new ProbeResult(probe, CheckStatus.Fail, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    private static SocketsHttpHandler CreateFreshHandler(ProbeType probe, TimeSpan timeout)
    {
        var sslOptions = new SslClientAuthenticationOptions();
        switch (probe)
        {
            case ProbeType.Http11:
                sslOptions.ApplicationProtocols = [SslApplicationProtocol.Http11];
                break;
            case ProbeType.Tls12:
                sslOptions.EnabledSslProtocols = SslProtocols.Tls12;
                break;
            case ProbeType.Tls13:
                sslOptions.EnabledSslProtocols = SslProtocols.Tls13;
                break;
        }

        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            AllowAutoRedirect = false,
            ConnectTimeout = timeout < TimeSpan.FromSeconds(2) ? timeout : TimeSpan.FromSeconds(2),
            SslOptions = sslOptions
        };
    }

    private static async Task<long?> PingAsync(string host, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }
}