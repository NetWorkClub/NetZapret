using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NetZapret.Core.Models;
using NetZapret.Core.Services;

namespace NetZapret.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsBackend : IPlatformBackend
{
    private const string ServiceName = "zapret";
    private const string RegistryServicePath = @"System\CurrentControlSet\Services\zapret";
    private const string RegistryStrategyValue = "zapret-discord-youtube";

    private static readonly string[] ConflictingBypassServices =
        ["GoodbyeDPI", "discordfix_zapret", "winws1", "winws2"];

    private Process? _activeProcess;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string HostsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public async Task<BypassStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var runningWinws = Process.GetProcessesByName("winws");
        var isWinwsRunning = runningWinws.Length > 0;
        foreach (var p in runningWinws) p.Dispose();

        var zapretState = await QueryServiceStateAsync(ServiceName, ct);
        var divState = await QueryServiceStateAsync("WinDivert", ct);

        var isServiceInstalled = zapretState != "NOT_INSTALLED";
        var isServiceRunning = zapretState == "RUNNING";

        string? activeStrategy = null;
        if (isServiceInstalled)
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryServicePath);
            activeStrategy = key?.GetValue(RegistryStrategyValue)?.ToString();
        }

        return new BypassStatus(
            IsBypassRunning: isWinwsRunning || isServiceRunning,
            IsServiceInstalled: isServiceInstalled,
            IsServiceRunning: isServiceRunning,
            ActiveStrategyId: activeStrategy,
            ActiveStrategyName: activeStrategy,
            DriverStatus: divState
        );
    }

    public async Task<(bool Success, string Message)> StartBypassAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default)
    {
        var zapretState = await QueryServiceStateAsync(ServiceName, ct);
        if (zapretState == "RUNNING")
            return (false, "Служба zapret уже запущена. Остановите или удалите её перед ручным запуском.");

        await StopBypassAsync(ct);
        await EnsureTcpTimestampsEnabledAsync(ct);
        ListManager.EnsureUserListsExist(settings.AssetsPath);

        var winDir = Path.Combine(settings.AssetsPath, "bin", "win");
        var exePath = Path.Combine(winDir, "winws.exe");
        if (!File.Exists(exePath))
            return (false, $"Не найден файл: {exePath}");

        var built = CommandBuilder.Build(manifest, strategy, settings, isWindows: true, useRelativePaths: true);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = settings.AssetsPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in built.Arguments)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        _activeProcess = process;

        await Task.Delay(450, ct);

        if (process.HasExited)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            var outMsg = await process.StandardOutput.ReadToEndAsync(ct);
            var details = string.IsNullOrWhiteSpace(err) ? outMsg : err;
            _activeProcess = null;
            process.Dispose();
            return (false, $"winws.exe завершился сразу после запуска: {details.Trim()}");
        }

        return (true, $"Стратегия \"{strategy.Name}\" запущена.");
    }

    public async Task StopBypassAsync(CancellationToken ct = default)
    {
        var hadRunning = false;

        if (_activeProcess is not null)
        {
            try
            {
                if (!_activeProcess.HasExited)
                {
                    hadRunning = true;
                    _activeProcess.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(3000);
                    await _activeProcess.WaitForExitAsync(cts.Token);
                }
            }
            catch
            {
            }
            finally
            {
                _activeProcess.Dispose();
                _activeProcess = null;
            }
        }

        foreach (var proc in Process.GetProcessesByName("winws"))
        {
            try
            {
                if (!proc.HasExited)
                {
                    hadRunning = true;
                    proc.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(3000);
                    await proc.WaitForExitAsync(cts.Token);
                }
            }
            catch
            {
            }
            finally
            {
                proc.Dispose();
            }
        }

        if (hadRunning)
            await Task.Delay(350, CancellationToken.None);
    }

    public async Task<(bool Success, string Message)> InstallServiceAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default)
    {
        if (!IsElevated)
            return (false, "Требуются права администратора для установки службы Windows.");

        await StopBypassAsync(ct);
        await EnsureTcpTimestampsEnabledAsync(ct);
        ListManager.EnsureUserListsExist(settings.AssetsPath);

        var shortAssets = ToShortPath(settings.AssetsPath);
        var effectiveSettings = settings with { AssetsPath = shortAssets };
        var winDir = Path.Combine(shortAssets, "bin", "win");
        var exePath = Path.Combine(winDir, "winws.exe");
        if (!File.Exists(exePath))
            return (false, $"Не найден файл: {exePath}");

        var built = CommandBuilder.Build(manifest, strategy, effectiveSettings, isWindows: true);
        var formattedArgs = string.Join(' ', built.Arguments.Select(QuoteIfContainsSpace));
        var binPathValue = $"\"{exePath}\" {formattedArgs}";

        await RunProcessAsync("sc.exe", ["stop", ServiceName], ct);
        await RunProcessAsync("sc.exe", ["delete", ServiceName], ct);

        var create = await RunProcessAsync(
            "sc.exe",
            ["create", ServiceName, "binPath=", binPathValue, "DisplayName=", "zapret", "start=", "auto"],
            ct);

        if (create.ExitCode != 0)
            return (false, $"Ошибка создания службы: {create.Output}");

        await RunProcessAsync("sc.exe", ["description", ServiceName, "NetZapret DPI bypass software"], ct);

        var start = await RunProcessAsync("sc.exe", ["start", ServiceName], ct);
        if (start.ExitCode != 0)
            return (false, $"Служба создана, но не запустилась: {start.Output}");

        using var key = Registry.LocalMachine.CreateSubKey(RegistryServicePath);
        key.SetValue(RegistryStrategyValue, strategy.Name, RegistryValueKind.String);

        return (true, $"Служба zapret установлена и запущена со стратегией \"{strategy.Name}\".");
    }

    public async Task<(bool Success, string Message)> RemoveServiceAsync(CancellationToken ct = default)
    {
        if (!IsElevated)
            return (false, "Требуются права администратора для удаления служб.");

        await StopBypassAsync(ct);
        await RunProcessAsync("sc.exe", ["stop", ServiceName], ct);
        await RunProcessAsync("sc.exe", ["delete", ServiceName], ct);
        await RunProcessAsync("sc.exe", ["stop", "WinDivert"], ct);
        await RunProcessAsync("sc.exe", ["delete", "WinDivert"], ct);
        await RunProcessAsync("sc.exe", ["stop", "WinDivert14"], ct);
        await RunProcessAsync("sc.exe", ["delete", "WinDivert14"], ct);

        return (true, "Службы zapret и WinDivert удалены.");
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(string assetsPath, CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();

        items.Add(IsElevated
            ? new DiagnosticItem("Права администратора", "Приложение запущено с правами администратора — драйвер WinDivert и службы доступны.", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Права администратора", "Запущено без прав администратора. Запуск обхода и установка службы будут заблокированы.", DiagnosticSeverity.Warning));

        var winDir = Path.Combine(assetsPath, "bin", "win");
        var missingFiles = new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" }
            .Where(f => !File.Exists(Path.Combine(winDir, f)))
            .ToList();

        items.Add(missingFiles.Count == 0
            ? new DiagnosticItem("Файлы движка Windows", "winws.exe, WinDivert.dll, WinDivert64.sys и cygwin1.dll найдены.", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Файлы движка Windows", $"Отсутствуют обязательные файлы в assets/bin/win: {string.Join(", ", missingFiles)}", DiagnosticSeverity.Error));

        if (Regex.IsMatch(assetsPath, @"[\u0400-\u04FF]"))
            items.Add(new DiagnosticItem("Кириллица в пути", $"Путь ({assetsPath}) содержит русские буквы. Для ручного запуска используются относительные пути, но для службы Windows лучше разместить программу в папке без кириллицы (например, C:\\NetZapret).", DiagnosticSeverity.Warning));
        else
            items.Add(new DiagnosticItem("Путь установки", "Путь не содержит кириллицы и полностью совместим со службой Windows.", DiagnosticSeverity.Ok));

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive) && assetsPath.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase))
            items.Add(new DiagnosticItem("Папка OneDrive", "Программа находится в синхронизируемой папке OneDrive — это может блокировать WinDivert64.sys. Перенесите в корень диска.", DiagnosticSeverity.Error));

        var bfeTask = QueryServiceStateAsync("BFE", ct);
        var tcpTask = RunProcessAsync("netsh.exe", ["interface", "tcp", "show", "global"], ct);
        var allServicesTask = RunProcessAsync("sc.exe", ["query", "type=", "service", "state=", "all"], ct);
        var zapretStateTask = QueryServiceStateAsync(ServiceName, ct);
        var divStateTask = QueryServiceStateAsync("WinDivert", ct);

        await Task.WhenAll(bfeTask, tcpTask, allServicesTask, zapretStateTask, divStateTask);

        items.Add(bfeTask.Result == "RUNNING"
            ? new DiagnosticItem("Base Filtering Engine (BFE)", "Системная служба фильтрации работает корректно.", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Base Filtering Engine (BFE)", "Служба BFE остановлена! Без неё драйвер WinDivert не сможет перехватывать трафик.", DiagnosticSeverity.Error));

        var tcpEnabled = IsTcpTimestampsEnabled(tcpTask.Result.Output);
        items.Add(tcpEnabled
            ? new DiagnosticItem("TCP Timestamps (RFC 1323)", "Включены — стратегии с fooling=ts будут работать штатно.", DiagnosticSeverity.Ok)
            : new DiagnosticItem("TCP Timestamps (RFC 1323)", "Отключены в Windows. Без них стратегии с фейками fooling=ts не сработают.", DiagnosticSeverity.Warning, "enable-timestamps", "Включить"));

        CheckProxy(items);
        CheckDoH(items);
        CheckConflicts(items, allServicesTask.Result.Output);
        CheckHosts(items);

        var winwsRunning = Process.GetProcessesByName("winws").Length > 0;
        if (!winwsRunning && divStateTask.Result is "RUNNING" or "STOP_PENDING")
        {
            items.Add(new DiagnosticItem(
                "Зависший драйвер WinDivert",
                "Процесс winws.exe уже закрыт, но драйвер WinDivert остался в памяти Windows. Нажмите «Удалить WinDivert».",
                DiagnosticSeverity.Warning,
                "cleanup-windivert",
                "Удалить WinDivert"));
        }

        return items;
    }

    public async Task<(bool Success, string Message)> ExecuteDiagnosticActionAsync(
        string actionId,
        string assetsPath,
        CancellationToken ct = default)
    {
        switch (actionId)
        {
            case "enable-timestamps":
                var ok = await EnsureTcpTimestampsEnabledAsync(ct);
                return ok
                    ? (true, "TCP Timestamps успешно включены.")
                    : (false, "Не удалось включить TCP Timestamps (нужны права администратора).");

            case "cleanup-windivert":
                await RunProcessAsync("sc.exe", ["stop", "WinDivert"], ct);
                await RunProcessAsync("sc.exe", ["delete", "WinDivert"], ct);
                await RunProcessAsync("sc.exe", ["stop", "WinDivert14"], ct);
                await RunProcessAsync("sc.exe", ["delete", "WinDivert14"], ct);
                return (true, "Драйвер WinDivert остановлен и выгружен.");

            case "remove-conflicts":
                foreach (var svc in ConflictingBypassServices)
                {
                    await RunProcessAsync("sc.exe", ["stop", svc], ct);
                    await RunProcessAsync("sc.exe", ["delete", svc], ct);
                }
                await RunProcessAsync("sc.exe", ["stop", "WinDivert"], ct);
                await RunProcessAsync("sc.exe", ["delete", "WinDivert"], ct);
                return (true, "Конфликтующие службы остановлены и удалены.");

            case "clear-discord":
                return await ClearDiscordCacheAsync(ct);

            default:
                return (false, "Неизвестное действие.");
        }
    }

    public async Task<(bool Success, string Message)> UpdateHostsAsync(string remoteHostsContent, CancellationToken ct = default)
    {
        if (!IsElevated)
            return (false, "Для изменения файла hosts нужны права администратора.");

        var current = File.Exists(HostsFilePath)
            ? await File.ReadAllTextAsync(HostsFilePath, Encoding.UTF8, ct)
            : string.Empty;

        if (!ListManager.IsHostsUpdateNeeded(current, remoteHostsContent))
            return (true, "Файл hosts уже содержит все актуальные записи.");

        if (File.Exists(HostsFilePath))
            File.Copy(HostsFilePath, HostsFilePath + ".netzapret.bak", overwrite: true);

        var updated = ListManager.MergeHostsContent(current, remoteHostsContent);
        await File.WriteAllTextAsync(HostsFilePath, updated, Encoding.UTF8, ct);

        return (true, "Файл hosts обновлён (создана резервная копия hosts.netzapret.bak).");
    }

    public Task<(bool Success, string Message)> ClearDiscordCacheAsync(CancellationToken ct = default)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var clients = new (string ProcessName, string FolderName)[]
        {
            ("Discord", "discord"),
            ("DiscordPTB", "discordptb"),
            ("DiscordCanary", "discordcanary"),
            ("DiscordDevelopment", "discorddevelopment")
        };

        var clearedCount = 0;
        var errors = new List<string>();

        foreach (var (procName, folder) in clients)
        {
            var baseDir = Path.Combine(appData, folder);
            if (!Directory.Exists(baseDir))
                continue;

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
                    errors.Add($"{folder}/{sub}: {ex.Message}");
                }
            }
        }

        if (errors.Count > 0)
            return Task.FromResult((false, $"Очищено: {clearedCount}, ошибки: {string.Join("; ", errors)}"));

        return Task.FromResult(clearedCount > 0
            ? (true, $"Кэш Discord очищен (папок: {clearedCount}).")
            : (true, "Кэш Discord не найден или уже пуст."));
    }

    private static string ToShortPath(string longPath)
    {
        var sb = new StringBuilder(1024);
        var len = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
        return len > 0 ? sb.ToString() : longPath;
    }

    private static bool IsTcpTimestampsEnabled(string netshOutput)
    {
        foreach (var line in netshOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("1323", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("timestamps", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.Contains("enabled", StringComparison.OrdinalIgnoreCase))
                return true;

            if (line.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return netshOutput.Contains("timestamps", StringComparison.OrdinalIgnoreCase) &&
               netshOutput.Contains("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckProxy(List<DiagnosticItem> items)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1;
        if (enabled)
        {
            var server = key?.GetValue("ProxyServer")?.ToString() ?? "";
            items.Add(new DiagnosticItem("Системный прокси", $"Включён системный прокси ({server}). Если вы не используете прокси/VPN, отключите его в параметрах сети Windows.", DiagnosticSeverity.Warning));
        }
        else
        {
            items.Add(new DiagnosticItem("Системный прокси", "Не используется — трафик идёт напрямую.", DiagnosticSeverity.Ok));
        }
    }

    private static void CheckDoH(List<DiagnosticItem> items)
    {
        var dohEnabled = false;
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
            if (baseKey is not null)
            {
                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    using var sub = baseKey.OpenSubKey(subName);
                    var flags = sub?.GetValue("DohFlags");
                    if (flags is not null && Convert.ToInt64(flags) > 0)
                    {
                        dohEnabled = true;
                        break;
                    }
                }
            }
        }
        catch
        {
        }

        items.Add(dohEnabled
            ? new DiagnosticItem("Шифрованный DNS (DoH)", "Включён на уровне Windows — подмена DNS провайдером невозможна.", DiagnosticSeverity.Ok)
            : new DiagnosticItem("Шифрованный DNS (DoH)", "Не настроен в параметрах Windows. Включите «Безопасный DNS» в браузере (Cloudflare/Google) или в параметрах сети Windows 11.", DiagnosticSeverity.Warning));
    }

    private static void CheckConflicts(List<DiagnosticItem> items, string servicesOutput)
    {
        var adguard = Process.GetProcessesByName("AdguardSvc");
        var hasAdguard = adguard.Length > 0;
        foreach (var p in adguard) p.Dispose();

        if (hasAdguard)
            items.Add(new DiagnosticItem("AdGuard", "Обнаружен процесс AdguardSvc.exe — его сетевой фильтр может конфликтовать с Discord и обходом.", DiagnosticSeverity.Error));

        if (servicesOutput.Contains("Killer", StringComparison.OrdinalIgnoreCase))
            items.Add(new DiagnosticItem("Killer Network", "Обнаружены службы Killer — конфликтуют с WinDivert.", DiagnosticSeverity.Error));

        if (servicesOutput.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            servicesOutput.Contains("Connectivity", StringComparison.OrdinalIgnoreCase))
            items.Add(new DiagnosticItem("Intel Connectivity", "Обнаружена служба Intel Connectivity Network Service — конфликтует с WinDivert.", DiagnosticSeverity.Error));

        if (servicesOutput.Contains("TracSrvWrapper", StringComparison.OrdinalIgnoreCase) ||
            servicesOutput.Contains("EPWD", StringComparison.OrdinalIgnoreCase))
            items.Add(new DiagnosticItem("Check Point", "Обнаружены службы Check Point VPN — конфликтуют с WinDivert.", DiagnosticSeverity.Error));

        if (servicesOutput.Contains("SmartByte", StringComparison.OrdinalIgnoreCase))
            items.Add(new DiagnosticItem("SmartByte", "Обнаружены службы SmartByte — конфликтуют с WinDivert.", DiagnosticSeverity.Error));

        var foundBypassConflicts = ConflictingBypassServices
            .Where(s => servicesOutput.Contains(s, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (foundBypassConflicts.Count > 0)
        {
            items.Add(new DiagnosticItem(
                "Конфликтующие обходы",
                $"Найдены сторонние службы обхода DPI: {string.Join(", ", foundBypassConflicts)}.",
                DiagnosticSeverity.Error,
                "remove-conflicts",
                "Удалить службы"));
        }
        else if (!hasAdguard)
        {
            items.Add(new DiagnosticItem("Конфликты ПО", "Сторонних драйверов и конфликтующих обходов не обнаружено.", DiagnosticSeverity.Ok));
        }
    }

    private void CheckHosts(List<DiagnosticItem> items)
    {
        if (!File.Exists(HostsFilePath))
            return;

        var content = File.ReadAllText(HostsFilePath);
        if (content.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new DiagnosticItem("Файл hosts", "В системном файле hosts найдены старые записи для youtube.com / youtu.be — они могут ломать доступ к YouTube.", DiagnosticSeverity.Warning));
        }
    }

    private static async Task<bool> EnsureTcpTimestampsEnabledAsync(CancellationToken ct)
    {
        var show = await RunProcessAsync("netsh.exe", ["interface", "tcp", "show", "global"], ct);
        if (IsTcpTimestampsEnabled(show.Output))
            return true;

        var set = await RunProcessAsync("netsh.exe", ["interface", "tcp", "set", "global", "timestamps=enabled"], ct);
        return set.ExitCode == 0;
    }

    private static async Task<string> QueryServiceStateAsync(string serviceName, CancellationToken ct)
    {
        var res = await RunProcessAsync("sc.exe", ["query", serviceName], ct);
        if (res.ExitCode != 0)
            return "NOT_INSTALLED";

        if (res.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            return "RUNNING";
        if (res.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
            return "STOP_PENDING";
        if (res.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            return "STOPPED";

        return "UNKNOWN";
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

        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var output = (await stdOutTask) + Environment.NewLine + (await stdErrTask);
        return (proc.ExitCode, output.Trim());
    }

    private static string QuoteIfContainsSpace(string arg)
    {
        if (!arg.Contains(' '))
            return arg;

        var eqIndex = arg.IndexOf('=');
        if (eqIndex > 0)
            return $"{arg[..(eqIndex + 1)]}\"{arg[(eqIndex + 1)..]}\"";

        return $"\"{arg}\"";
    }
}