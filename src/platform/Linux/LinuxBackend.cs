using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NetZapret.Core.Models;
using NetZapret.Core.Services;

namespace NetZapret.Platform.Linux;

[SupportedOSPlatform("linux")]
public sealed class LinuxBackend : IPlatformBackend, ISessionPlatformBackend
{
    private const string ServiceName = "netzapret.service";
    private const string SavedStrategyPath = "/etc/netzapret/strategy.txt";
    private const string PidFilePath = "/run/netzapret.pid";

    private Process? _sessionProcess;

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public string HostsFilePath => "/etc/hosts";

    public async Task<BypassStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nfqwsProcs = Process.GetProcessesByName("nfqws");
        var isProcRunning = nfqwsProcs.Length > 0;
        foreach (var p in nfqwsProcs) p.Dispose();

        var isPidRunning = File.Exists(PidFilePath);

        var enabledRes = await RunProcessAsync("systemctl", ["is-enabled", ServiceName], ct);
        var activeRes = await RunProcessAsync("systemctl", ["is-active", ServiceName], ct);

        var isServiceInstalled = enabledRes.ExitCode == 0 || File.Exists($"/etc/systemd/system/{ServiceName}");
        var isServiceRunning = activeRes.ExitCode == 0 && activeRes.Output.Trim() == "active";

        string? activeStrategy = null;
        if (File.Exists(SavedStrategyPath))
        {
            try
            {
                activeStrategy = (await File.ReadAllTextAsync(SavedStrategyPath, ct)).Trim();
            }
            catch
            {
            }
        }

        var driverStatus = isProcRunning || isPidRunning || isServiceRunning ? "NFQUEUE ACTIVE" : "STOPPED";

        return new BypassStatus(
            IsBypassRunning: isProcRunning || isPidRunning || isServiceRunning || _sessionProcess is { HasExited: false },
            IsServiceInstalled: isServiceInstalled,
            IsServiceRunning: isServiceRunning,
            ActiveStrategyId: activeStrategy,
            ActiveStrategyName: activeStrategy,
            DriverStatus: driverStatus
        );
    }

    public async Task<(bool Success, string Message)> BeginSessionAsync(CancellationToken ct = default)
    {
        if (_sessionProcess is { HasExited: false })
            return (true, "OK");

        var daemonPath = FindDaemonPath();
        if (daemonPath is null)
            return (false, "Не найден исполняемый файл NetZapret.Daemon.");

        var psi = CreateElevatedStartInfo(daemonPath, ["session"], redirectInput: true);
        _sessionProcess = Process.Start(psi);
        if (_sessionProcess is null)
            return (false, "Не удалось запустить привилегированную сессию.");

        await Task.Delay(150, ct);
        if (_sessionProcess.HasExited)
        {
            var err = await _sessionProcess.StandardError.ReadToEndAsync(ct);
            _sessionProcess.Dispose();
            _sessionProcess = null;
            return (false, string.IsNullOrWhiteSpace(err) ? "Авторизация отменена." : err.Trim());
        }

        return (true, "OK");
    }

    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        if (_sessionProcess is null)
            return;

        try
        {
            if (!_sessionProcess.HasExited)
            {
                await _sessionProcess.StandardInput.WriteLineAsync("QUIT");
                await _sessionProcess.StandardInput.FlushAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                await _sessionProcess.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
            try
            {
                if (!_sessionProcess.HasExited)
                    _sessionProcess.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        finally
        {
            _sessionProcess.Dispose();
            _sessionProcess = null;
        }
    }

    public async Task<(bool Success, string Message)> StartBypassAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default)
    {
        var activeRes = await RunProcessAsync("systemctl", ["is-active", ServiceName], ct);
        if (activeRes.ExitCode == 0 && activeRes.Output.Trim() == "active")
            return (false, "Служба netzapret.service уже запущена. Удалите или остановите её перед тестами.");

        var nfqwsPath = FindNfqwsPath(settings.AssetsPath);
        if (nfqwsPath is null)
            return (false, "Не найден бинарник nfqws. Положите его в assets/bin/linux/nfqws или установите zapret.");

        ListManager.EnsureUserListsExist(settings.AssetsPath);
        var built = CommandBuilder.Build(manifest, strategy, settings, isWindows: false);

        if (_sessionProcess is { HasExited: false })
        {
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(built.Arguments));
            var cmdLine = $"START\t{nfqwsPath}\t{built.TcpPortsForFirewall}\t{built.UdpPortsForFirewall}\t{settings.LinuxQueueNum}\t{payload}";
            await _sessionProcess.StandardInput.WriteLineAsync(cmdLine);
            await _sessionProcess.StandardInput.FlushAsync(ct);

            var resp = await _sessionProcess.StandardOutput.ReadLineAsync(ct);
            if (resp is null)
                return (false, "Сессия NetZapret.Daemon неожиданно завершилась.");

            return resp == "OK"
                ? (true, $"Стратегия \"{strategy.Name}\" запущена.")
                : (false, resp.StartsWith("ERR:", StringComparison.Ordinal) ? resp[4..] : resp);
        }

        var daemonPath = FindDaemonPath();
        if (daemonPath is null)
            return (false, "Не найден исполняемый файл NetZapret.Daemon.");

        var args = new List<string>
        {
            "start",
            "--nfqws", nfqwsPath,
            "--tcp", built.TcpPortsForFirewall,
            "--udp", built.UdpPortsForFirewall,
            "--qnum", settings.LinuxQueueNum.ToString(),
            "--"
        };
        args.AddRange(built.Arguments);

        var res = await RunDaemonAsync(daemonPath, args, ct);
        return res.ExitCode == 0
            ? (true, $"Стратегия \"{strategy.Name}\" запущена.")
            : (false, string.IsNullOrWhiteSpace(res.Output) ? "Не удалось запустить nfqws." : res.Output);
    }

    public async Task StopBypassAsync(CancellationToken ct = default)
    {
        if (_sessionProcess is { HasExited: false })
        {
            try
            {
                await _sessionProcess.StandardInput.WriteLineAsync("STOP");
                await _sessionProcess.StandardInput.FlushAsync(ct);
                await _sessionProcess.StandardOutput.ReadLineAsync(ct);
                return;
            }
            catch
            {
            }
        }

        var daemonPath = FindDaemonPath();
        if (daemonPath is null)
            return;

        await RunDaemonAsync(daemonPath, ["stop"], ct);
    }

    public async Task<(bool Success, string Message)> InstallServiceAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default)
    {
        var nfqwsPath = FindNfqwsPath(settings.AssetsPath);
        if (nfqwsPath is null)
            return (false, "Не найден бинарник nfqws. Положите его в assets/bin/linux/nfqws.");

        var daemonPath = FindDaemonPath();
        if (daemonPath is null)
            return (false, "Не найден исполняемый файл NetZapret.Daemon.");

        ListManager.EnsureUserListsExist(settings.AssetsPath);
        var built = CommandBuilder.Build(manifest, strategy, settings, isWindows: false);

        var args = new List<string>
        {
            "install-service",
            "--nfqws", nfqwsPath,
            "--tcp", built.TcpPortsForFirewall,
            "--udp", built.UdpPortsForFirewall,
            "--qnum", settings.LinuxQueueNum.ToString(),
            "--strategy", strategy.Name,
            "--"
        };
        args.AddRange(built.Arguments);

        var res = await RunDaemonAsync(daemonPath, args, ct);
        return res.ExitCode == 0
            ? (true, $"Systemd-служба netzapret установлена со стратегией \"{strategy.Name}\".")
            : (false, string.IsNullOrWhiteSpace(res.Output) ? "Ошибка установки службы." : res.Output);
    }

    public async Task<(bool Success, string Message)> RemoveServiceAsync(CancellationToken ct = default)
    {
        var daemonPath = FindDaemonPath();
        if (daemonPath is null)
            return (false, "Не найден исполняемый файл NetZapret.Daemon.");

        var res = await RunDaemonAsync(daemonPath, ["remove-service"], ct);
        return res.ExitCode == 0
            ? (true, "Служба netzapret удалена и правила nftables очищены.")
            : (false, res.Output);
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(string assetsPath, CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();

        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
        items.Add(new DiagnosticItem("Сессия рабочего стола", $"Тип: {sessionType}", DiagnosticSeverity.Ok));

        var nfqwsPath = FindNfqwsPath(assetsPath);
        items.Add(nfqwsPath is not null
            ? new DiagnosticItem("Движок nfqws", $"Найден: {nfqwsPath}", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Движок nfqws", "Не найден в assets/bin/linux/nfqws или /usr/bin/nfqws", DiagnosticSeverity.Error));

        var nftPath = FindSystemBinary("nft");
        items.Add(nftPath is not null
            ? new DiagnosticItem("nftables (nft)", $"Найден: {nftPath}", DiagnosticSeverity.Ok)
            : new DiagnosticItem("nftables (nft)", "Утилита nft не найдена. Установите пакет nftables", DiagnosticSeverity.Error));

        var pkexecPath = FindSystemBinary("pkexec");
        items.Add(pkexecPath is not null || IsElevated
            ? new DiagnosticItem("Повышение прав", IsElevated ? "Запущено с правами root" : $"Polkit найден: {pkexecPath}", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Повышение прав", "pkexec не найден, запуск правил без root невозможен", DiagnosticSeverity.Error));

        var daemonPath = FindDaemonPath();
        items.Add(daemonPath is not null
            ? new DiagnosticItem("NetZapret.Daemon", $"Найден: {daemonPath}", DiagnosticSeverity.Ok)
            : new DiagnosticItem("NetZapret.Daemon", "Не найден рядом с приложением", DiagnosticSeverity.Error));

        var proxyVars = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy" }
            .Select(v => (Name: v, Val: Environment.GetEnvironmentVariable(v)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Val))
            .ToList();

        items.Add(proxyVars.Count > 0
            ? new DiagnosticItem("Переменные прокси", string.Join(", ", proxyVars.Select(p => $"{p.Name}={p.Val}")), DiagnosticSeverity.Warning)
            : new DiagnosticItem("Переменные прокси", "Системные переменные прокси не заданы", DiagnosticSeverity.Ok));

        var vpnIfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.Name)
            .Where(name =>
                name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("amnezia", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase))
            .ToList();

        items.Add(vpnIfaces.Count > 0
            ? new DiagnosticItem("Активные VPN-интерфейсы", $"Обнаружены: {string.Join(", ", vpnIfaces)} (могут конфликтовать с обходом)", DiagnosticSeverity.Warning)
            : new DiagnosticItem("VPN-интерфейсы", "Конфликтующих туннелей не обнаружено", DiagnosticSeverity.Ok));

        if (File.Exists(HostsFilePath))
        {
            var hostsText = await File.ReadAllTextAsync(HostsFilePath, ct);
            if (hostsText.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                hostsText.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new DiagnosticItem("Файл /etc/hosts", "Содержит записи для youtube.com / youtu.be — это может мешать доступу", DiagnosticSeverity.Warning));
            }
            else
            {
                items.Add(new DiagnosticItem("Файл /etc/hosts", "Конфликтов с YouTube нет", DiagnosticSeverity.Ok));
            }
        }

        var nfqueueProc = File.Exists("/proc/net/netfilter/nfnetlink_queue");
        if (nfqueueProc)
            items.Add(new DiagnosticItem("Модуль ядра nfnetlink_queue", "Активен", DiagnosticSeverity.Ok));

        return items;
    }

    public Task<(bool Success, string Message)> ExecuteDiagnosticActionAsync(
        string actionId,
        string assetsPath,
        CancellationToken ct = default)
    {
        if (actionId == "clear-discord")
            return ClearDiscordCacheAsync(ct);

        return Task.FromResult((false, "Действие недоступно для Linux."));
    }

    public async Task<(bool Success, string Message)> UpdateHostsAsync(string remoteHostsContent, CancellationToken ct = default)
    {
        var current = File.Exists(HostsFilePath)
            ? await File.ReadAllTextAsync(HostsFilePath, Encoding.UTF8, ct)
            : string.Empty;

        if (!ListManager.IsHostsUpdateNeeded(current, remoteHostsContent))
            return (true, "Файл /etc/hosts уже актуален.");

        var updated = ListManager.MergeHostsContent(current, remoteHostsContent);
        var tempFile = Path.Combine(Path.GetTempPath(), $"netzapret_hosts_{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempFile, updated, Encoding.UTF8, ct);
            var daemonPath = FindDaemonPath();
            if (daemonPath is null)
                return (false, "Не найден исполняемый файл NetZapret.Daemon.");

            var res = await RunDaemonAsync(daemonPath, ["update-hosts", tempFile], ct);
            return res.ExitCode == 0
                ? (true, "Файл /etc/hosts обновлён (создана резервная копия /etc/hosts.netzapret.bak).")
                : (false, res.Output);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
            }
        }
    }

    public Task<(bool Success, string Message)> ClearDiscordCacheAsync(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(home, ".config", "discord"),
            Path.Combine(home, ".config", "discordptb"),
            Path.Combine(home, ".config", "discordcanary"),
            Path.Combine(home, ".var", "app", "com.discordapp.Discord", "config", "discord")
        };

        foreach (var procName in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        var clearedCount = 0;
        var errors = new List<string>();

        foreach (var baseDir in dirs)
        {
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var target = Path.Combine(baseDir, sub);
                if (!Directory.Exists(target))
                    continue;

                try
                {
                    Directory.Delete(target, recursive: true);
                    clearedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{target}: {ex.Message}");
                }
            }
        }

        if (errors.Count > 0)
            return Task.FromResult((false, $"Очищено: {clearedCount}, ошибки: {string.Join("; ", errors)}"));

        return Task.FromResult(clearedCount > 0
            ? (true, $"Кэш Discord очищен (папок: {clearedCount}).")
            : (true, "Кэш Discord не найден или уже пуст."));
    }

    private static string? FindNfqwsPath(string assetsPath)
    {
        var local = Path.Combine(assetsPath, "bin", "linux", "nfqws");
        if (File.Exists(local))
            return local;

        var candidates = new[] { "/usr/local/bin/nfqws", "/usr/bin/nfqws", "/opt/zapret/nfq/nfqws" };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindDaemonPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var besideUi = Path.Combine(baseDir, "NetZapret.Daemon");
        if (File.Exists(besideUi))
            return besideUi;

        var devDebug = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "daemon", "bin", "Debug", "net8.0", "NetZapret.Daemon"));
        if (File.Exists(devDebug))
            return devDebug;

        var devRelease = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "daemon", "bin", "Release", "net8.0", "NetZapret.Daemon"));
        if (File.Exists(devRelease))
            return devRelease;

        return null;
    }

    private static string? FindSystemBinary(string name)
    {
        var candidates = new[] { $"/usr/sbin/{name}", $"/sbin/{name}", $"/usr/bin/{name}", $"/bin/{name}", $"/usr/local/bin/{name}" };
        return candidates.FirstOrDefault(File.Exists);
    }

    private ProcessStartInfo CreateElevatedStartInfo(string daemonPath, IEnumerable<string> args, bool redirectInput)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (IsElevated)
        {
            psi.FileName = daemonPath;
            foreach (var a in args)
                psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = "pkexec";
            psi.ArgumentList.Add(daemonPath);
            foreach (var a in args)
                psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private async Task<(int ExitCode, string Output)> RunDaemonAsync(
        string daemonPath,
        IEnumerable<string> args,
        CancellationToken ct)
    {
        var psi = CreateElevatedStartInfo(daemonPath, args, redirectInput: false);
        using var proc = Process.Start(psi);
        if (proc is null)
            return (-1, string.Empty);

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, ((await outTask) + Environment.NewLine + (await errTask)).Trim());
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IEnumerable<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null)
            return (-1, string.Empty);

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, ((await outTask) + Environment.NewLine + (await errTask)).Trim());
    }
}