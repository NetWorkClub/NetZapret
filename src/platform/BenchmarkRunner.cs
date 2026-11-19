using System.Text;
using NetZapret.Core.Models;
using NetZapret.Core.Services;

namespace NetZapret.Platform;

public enum BenchmarkMode
{
    Standard,
    Quick,
    Dpi1620
}

public sealed record BenchmarkProgress(
    int CurrentIndex,
    int TotalCount,
    string StrategyId,
    string StrategyName,
    string StageMessage,
    StrategyBenchmarkSummary? CompletedSummary = null,
    StrategyRunDetail? CompletedDetail = null
);

public sealed record StrategyRunDetail(
    StrategyDefinition Strategy,
    StrategyBenchmarkSummary Summary,
    IReadOnlyList<TargetCheckResult>? StandardResults,
    IReadOnlyList<DpiCheckResult>? DpiResults,
    string? Error
);

public sealed record BenchmarkBatchResult(
    BenchmarkMode Mode,
    IReadOnlyList<StrategyRunDetail> Details,
    StrategyBenchmarkSummary? BestStrategy,
    string ReportFilePath
);

public static class BenchmarkRunner
{
    public static async Task<BenchmarkBatchResult> RunAsync(
        IPlatformBackend backend,
        StrategyManifest manifest,
        IReadOnlyList<StrategyDefinition> strategies,
        LaunchSettings settings,
        BenchmarkMode mode,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var targetsPath = BenchmarkEngine.GetEffectiveTargetsPath(settings.AssetsPath);
        var allTargets = BenchmarkEngine.LoadTargets(targetsPath);
        var standardTargets = mode == BenchmarkMode.Quick
            ? BenchmarkEngine.SelectQuickTargets(allTargets)
            : allTargets;

        IReadOnlyList<DpiSuiteEntry> dpiTargets = [];
        if (mode == BenchmarkMode.Dpi1620)
        {
            progress?.Report(new BenchmarkProgress(0, strategies.Count, "", "", "Загрузка списка целей DPI 16-20..."));
            dpiTargets = await BenchmarkEngine.FetchDpiSuiteAsync(TimeSpan.FromSeconds(6), ct);
            if (dpiTargets.Count == 0)
                throw new InvalidOperationException("Не удалось загрузить список целей DPI 16-20.");
        }

        var originalIpset = ListManager.GetIpsetMode(settings.AssetsPath);
        var ipsetSwitched = false;
        var startedSession = false;

        if (backend is ISessionPlatformBackend sessionBackend)
        {
            progress?.Report(new BenchmarkProgress(0, strategies.Count, "", "", "Открытие привилегированной сессии..."));
            var (ok, msg) = await sessionBackend.BeginSessionAsync(ct);
            if (!ok)
                throw new InvalidOperationException(msg);
            startedSession = true;
        }

        var details = new List<StrategyRunDetail>();

        try
        {
            if (mode == BenchmarkMode.Dpi1620 && originalIpset != IpsetMode.Any)
            {
                ListManager.SetIpsetMode(settings.AssetsPath, IpsetMode.Any);
                ipsetSwitched = true;
            }

            for (var i = 0; i < strategies.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var strategy = strategies[i];
                var step = i + 1;

                progress?.Report(new BenchmarkProgress(
                    step, strategies.Count, strategy.Id, strategy.Name,
                    $"[{step}/{strategies.Count}] Запуск {strategy.Name}..."));

                await backend.StopBypassAsync(CancellationToken.None);
                var (started, startMsg) = await backend.StartBypassAsync(manifest, strategy, settings, ct);

                if (!started)
                {
                    var failSummary = new StrategyBenchmarkSummary(strategy.Id, strategy.Name, 0, 1, 0, 0, 0, 9999);
                    var failDetail = new StrategyRunDetail(strategy, failSummary, null, null, startMsg);
                    details.Add(failDetail);
                    progress?.Report(new BenchmarkProgress(
                        step, strategies.Count, strategy.Id, strategy.Name,
                        $"Ошибка запуска: {strategy.Name}", failSummary, failDetail));
                    continue;
                }

                await Task.Delay(600, ct);

                progress?.Report(new BenchmarkProgress(
                    step, strategies.Count, strategy.Id, strategy.Name,
                    $"[{step}/{strategies.Count}] Проверка {strategy.Name}..."));

                StrategyBenchmarkSummary summary;
                IReadOnlyList<TargetCheckResult>? stdRes = null;
                IReadOnlyList<DpiCheckResult>? dpiRes = null;

                if (mode == BenchmarkMode.Dpi1620)
                {
                    dpiRes = await BenchmarkEngine.RunDpiSuiteAsync(dpiTargets, TimeSpan.FromSeconds(5), 65536, 8, ct);
                    summary = BenchmarkEngine.SummarizeDpi(strategy.Id, strategy.Name, dpiRes);
                }
                else
                {
                    stdRes = await BenchmarkEngine.RunStandardAsync(standardTargets, TimeSpan.FromSeconds(4), 12, ct);
                    summary = BenchmarkEngine.SummarizeStandard(strategy.Id, strategy.Name, stdRes);
                }

                await backend.StopBypassAsync(CancellationToken.None);

                var runDetail = new StrategyRunDetail(strategy, summary, stdRes, dpiRes, null);
                details.Add(runDetail);
                progress?.Report(new BenchmarkProgress(
                    step, strategies.Count, strategy.Id, strategy.Name,
                    $"[{step}/{strategies.Count}] Готово: {strategy.Name}", summary, runDetail));
            }
        }
        finally
        {
            await backend.StopBypassAsync(CancellationToken.None);

            if (ipsetSwitched)
                ListManager.SetIpsetMode(settings.AssetsPath, originalIpset);

            if (startedSession && backend is ISessionPlatformBackend sb)
                await sb.EndSessionAsync(CancellationToken.None);
        }

        var best = SelectBest(details, mode);
        var reportPath = SaveReport(settings.AssetsPath, mode, details, best);

        return new BenchmarkBatchResult(mode, details, best, reportPath);
    }

    private static StrategyBenchmarkSummary? SelectBest(IReadOnlyList<StrategyRunDetail> details, BenchmarkMode mode)
    {
        var valid = details.Where(d => d.Error is null).Select(d => d.Summary).ToList();
        if (valid.Count == 0)
            return null;

        if (mode == BenchmarkMode.Dpi1620)
        {
            return valid
                .OrderByDescending(s => s.OkCount)
                .ThenBy(s => s.BlockedCount)
                .ThenBy(s => s.FailCount)
                .FirstOrDefault();
        }

        return valid
            .OrderByDescending(s => s.OkCount)
            .ThenByDescending(s => s.PingOkCount)
            .ThenBy(s => s.AveragePingMs)
            .FirstOrDefault();
    }

    private static string SaveReport(
        string assetsPath,
        BenchmarkMode mode,
        IReadOnlyList<StrategyRunDetail> details,
        StrategyBenchmarkSummary? best)
    {
        var resultsDir = Path.Combine(assetsPath, "results");
        Directory.CreateDirectory(resultsDir);

        var fileName = $"test_results_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        var fullPath = Path.Combine(resultsDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"Mode: {mode}");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var d in details)
        {
            sb.AppendLine($"Config: {d.Strategy.Name} ({d.Strategy.LegacyName ?? d.Strategy.Id})");
            if (d.Error is not null)
            {
                sb.AppendLine($"  ERROR: {d.Error}");
                sb.AppendLine();
                continue;
            }

            if (d.StandardResults is not null)
            {
                foreach (var r in d.StandardResults)
                {
                    var probes = string.Join(' ', r.Probes.Select(p => $"{p.Probe}:{p.Status}"));
                    var ping = r.PingMs is { } ms ? $"{ms} ms" : "Timeout";
                    sb.AppendLine($"  {r.Target.Name} : {probes} | Ping: {ping}");
                }
            }

            if (d.DpiResults is not null)
            {
                foreach (var r in d.DpiResults)
                {
                    sb.AppendLine($"  Target: [{r.Target.Country}] {r.Target.Id} ({r.Target.Provider})");
                    foreach (var p in r.Probes)
                        sb.AppendLine($"    {p.Probe}: status={p.Status} time={p.ElapsedMs:F0}ms detail={p.Detail}");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("=== ANALYTICS ===");
        foreach (var d in details)
        {
            var s = d.Summary;
            if (mode == BenchmarkMode.Dpi1620)
                sb.AppendLine($"{s.StrategyName} : OK: {s.OkCount}, FAIL: {s.FailCount}, UNSUP: {s.UnsupportedCount}, BLOCKED: {s.BlockedCount}");
            else
                sb.AppendLine($"{s.StrategyName} : HTTP OK: {s.OkCount}, ERR: {s.FailCount}, UNSUP: {s.UnsupportedCount}, Ping OK: {s.PingOkCount}, AvgPing: {s.AveragePingMs:F0}ms");
        }

        sb.AppendLine();
        sb.AppendLine($"Best strategy: {best?.StrategyName ?? "none"}");

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        return fullPath;
    }
}