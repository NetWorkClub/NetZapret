using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetZapret.Daemon;

public static class Program
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private const string NftTableName = "netzapret";
    private const string PidFilePath = "/run/netzapret.pid";
    private const string RuntimeNftPath = "/run/netzapret.nft";
    private const string SystemdUnitPath = "/etc/systemd/system/netzapret.service";
    private const string ConfigDir = "/etc/netzapret";
    private const string SavedNftPath = "/etc/netzapret/rules.nft";
    private const string SavedStrategyPath = "/etc/netzapret/strategy.txt";
    private const string HostsPath = "/etc/hosts";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return 1;

        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return cmd switch
        {
            "start" => await HandleStartAsync(rest),
            "stop" => await HandleStopAsync(),
            "session" => await HandleSessionAsync(),
            "install-service" => await HandleInstallServiceAsync(rest),
            "remove-service" => await HandleRemoveServiceAsync(),
            "update-hosts" => await HandleUpdateHostsAsync(rest),
            _ => 1
        };
    }

    private static async Task<int> HandleStartAsync(string[] args)
    {
        if (!TryParseStartArgs(args, out var nfqwsPath, out var tcp, out var udp, out var qnum, out _, out var nfqwsArgs, out var err))
        {
            Console.Error.WriteLine(err);
            return 1;
        }

        var (ok, msg) = await StartNfqwsAndFirewallAsync(nfqwsPath, tcp, udp, qnum, nfqwsArgs);
        if (!ok)
        {
            Console.Error.WriteLine(msg);
            return 1;
        }

        Console.WriteLine(msg);
        return 0;
    }

    private static async Task<int> HandleStopAsync()
    {
        await StopNfqwsAndFirewallAsync();
        Console.WriteLine("OK");
        return 0;
    }

    private static async Task<int> HandleSessionAsync()
    {
        try
        {
            while (true)
            {
                var line = await Console.In.ReadLineAsync();
                if (line is null)
                    break;

                if (string.Equals(line, "QUIT", StringComparison.Ordinal))
                {
                    await StopNfqwsAndFirewallAsync();
                    Console.WriteLine("OK");
                    break;
                }

                if (string.Equals(line, "STOP", StringComparison.Ordinal))
                {
                    await StopNfqwsAndFirewallAsync();
                    Console.WriteLine("OK");
                    continue;
                }

                if (line.StartsWith("START\t", StringComparison.Ordinal))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 6 || !int.TryParse(parts[4], out var qnum))
                    {
                        Console.WriteLine("ERR:Некорректная команда START");
                        continue;
                    }

                    try
                    {
                        var jsonBytes = Convert.FromBase64String(parts[5]);
                        var nfqwsArgs = JsonSerializer.Deserialize<List<string>>(jsonBytes) ?? [];
                        var (ok, msg) = await StartNfqwsAndFirewallAsync(parts[1], parts[2], parts[3], qnum, nfqwsArgs);
                        Console.WriteLine(ok ? "OK" : $"ERR:{msg.Replace('\n', ' ')}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERR:{ex.Message}");
                    }
                    continue;
                }

                Console.WriteLine("ERR:Неизвестная команда");
            }
        }
        finally
        {
            await StopNfqwsAndFirewallAsync();
        }

        return 0;
    }

    private static async Task<int> HandleInstallServiceAsync(string[] args)
    {
        if (!TryParseStartArgs(args, out var nfqwsPath, out var tcp, out var udp, out var qnum, out var strategyName, out var nfqwsArgs, out var err))
        {
            Console.Error.WriteLine(err);
            return 1;
        }

        await StopNfqwsAndFirewallAsync();
        EnsureExecutable(nfqwsPath);

        var nftBin = FindBinary("nft") ?? "/usr/sbin/nft";
        Directory.CreateDirectory(ConfigDir);

        await File.WriteAllTextAsync(SavedNftPath, BuildNftScript(tcp, udp, qnum), Utf8NoBom);
        await File.WriteAllTextAsync(SavedStrategyPath, strategyName, Utf8NoBom);

        var execStartArgs = string.Join(' ', nfqwsArgs.Select(SystemdEscape));
        var unit = new StringBuilder()
            .AppendLine("[Unit]")
            .AppendLine("Description=NetZapret DPI bypass service")
            .AppendLine("After=network-online.target")
            .AppendLine("Wants=network-online.target")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=simple")
            .AppendLine($"ExecStartPre=-{nftBin} delete table inet {NftTableName}")
            .AppendLine($"ExecStartPre={nftBin} -f {SavedNftPath}")
            .AppendLine($"ExecStart={SystemdEscape(nfqwsPath)} --dpi-desync-fwmark=0x40000000 {execStartArgs}")
            .AppendLine($"ExecStopPost=-{nftBin} delete table inet {NftTableName}")
            .AppendLine("Restart=on-failure")
            .AppendLine("RestartSec=3")
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=multi-user.target")
            .ToString();

        await File.WriteAllTextAsync(SystemdUnitPath, unit, Utf8NoBom);

        await RunProcessAsync("systemctl", ["daemon-reload"]);
        var enable = await RunProcessAsync("systemctl", ["enable", "--now", "netzapret.service"]);
        if (enable.ExitCode != 0)
        {
            Console.Error.WriteLine(enable.Output);
            return 1;
        }

        Console.WriteLine("OK");
        return 0;
    }

    private static async Task<int> HandleRemoveServiceAsync()
    {
        await RunProcessAsync("systemctl", ["disable", "--now", "netzapret.service"]);

        if (File.Exists(SystemdUnitPath))
            File.Delete(SystemdUnitPath);

        if (File.Exists(SavedStrategyPath))
            File.Delete(SavedStrategyPath);

        if (File.Exists(SavedNftPath))
            File.Delete(SavedNftPath);

        await RunProcessAsync("systemctl", ["daemon-reload"]);
        await StopNfqwsAndFirewallAsync();

        Console.WriteLine("OK");
        return 0;
    }

    private static async Task<int> HandleUpdateHostsAsync(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Файл с новым содержимым hosts не найден.");
            return 1;
        }

        var updated = await File.ReadAllTextAsync(args[0], Encoding.UTF8);
        if (File.Exists(HostsPath))
            File.Copy(HostsPath, HostsPath + ".netzapret.bak", overwrite: true);

        await File.WriteAllTextAsync(HostsPath, updated, Utf8NoBom);
        Console.WriteLine("OK");
        return 0;
    }

    private static async Task<(bool Success, string Message)> StartNfqwsAndFirewallAsync(
        string nfqwsPath,
        string tcpPorts,
        string udpPorts,
        int qnum,
        IReadOnlyList<string> nfqwsArgs)
    {
        await StopNfqwsAndFirewallAsync();

        if (!File.Exists(nfqwsPath))
            return (false, $"Не найден бинарник nfqws: {nfqwsPath}");

        EnsureExecutable(nfqwsPath);

        var nftBin = FindBinary("nft");
        if (nftBin is null)
            return (false, "Утилита nft (nftables) не найдена.");

        await File.WriteAllTextAsync(RuntimeNftPath, BuildNftScript(tcpPorts, udpPorts, qnum), Utf8NoBom);
        var nftRes = await RunProcessAsync(nftBin, ["-f", RuntimeNftPath]);
        if (nftRes.ExitCode != 0)
            return (false, $"Ошибка применения nftables: {nftRes.Output}");

        var psi = new ProcessStartInfo
        {
            FileName = nfqwsPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("--daemon");
        psi.ArgumentList.Add($"--pidfile={PidFilePath}");
        psi.ArgumentList.Add("--dpi-desync-fwmark=0x40000000");
        foreach (var a in nfqwsArgs)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            await DeleteNftTableAsync(nftBin);
            return (false, "Не удалось запустить процесс nfqws.");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            await DeleteNftTableAsync(nftBin);
            var err = (await errTask).Trim();
            var stdOut = (await outTask).Trim();
            return (false, string.IsNullOrEmpty(err) ? stdOut : err);
        }

        await Task.Delay(200);
        if (!File.Exists(PidFilePath))
        {
            await DeleteNftTableAsync(nftBin);
            return (false, "nfqws завершился без создания PID-файла.");
        }

        return (true, "OK");
    }

    private static async Task StopNfqwsAndFirewallAsync()
    {
        if (File.Exists(PidFilePath))
        {
            var pidText = (await File.ReadAllTextAsync(PidFilePath)).Trim();
            if (int.TryParse(pidText, out var pid))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                    await p.WaitForExitAsync();
                }
                catch
                {
                }
            }

            try
            {
                File.Delete(PidFilePath);
            }
            catch
            {
            }
        }

        foreach (var p in Process.GetProcessesByName("nfqws"))
        {
            try
            {
                p.Kill();
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        if (File.Exists(RuntimeNftPath))
        {
            try
            {
                File.Delete(RuntimeNftPath);
            }
            catch
            {
            }
        }

        var nftBin = FindBinary("nft");
        if (nftBin is not null)
            await DeleteNftTableAsync(nftBin);
    }

    private static Task DeleteNftTableAsync(string nftBin) =>
        RunProcessAsync(nftBin, ["delete", "table", "inet", NftTableName]);

    private static string BuildNftScript(string tcpPorts, string udpPorts, int qnum)
    {
        var mergedTcp = MergePortRanges(tcpPorts);
        var mergedUdp = MergePortRanges(udpPorts);

        var sb = new StringBuilder();
        sb.AppendLine($"add table inet {NftTableName}");
        sb.AppendLine($"flush table inet {NftTableName}");
        sb.AppendLine($"table inet {NftTableName} {{");
        sb.AppendLine("    chain postrouting {");
        sb.AppendLine("        type filter hook postrouting priority mangle; policy accept;");
        sb.AppendLine("        oifname \"lo\" return");
        sb.AppendLine("        meta mark and 0x40000000 != 0 return");

        if (mergedTcp.Count > 0)
        {
            var expr = mergedTcp.Count == 1 ? mergedTcp[0] : "{ " + string.Join(", ", mergedTcp) + " }";
            sb.AppendLine($"        tcp dport {expr} ct original packets 1-20 queue num {qnum} bypass");
        }

        if (mergedUdp.Count > 0)
        {
            var expr = mergedUdp.Count == 1 ? mergedUdp[0] : "{ " + string.Join(", ", mergedUdp) + " }";
            sb.AppendLine($"        udp dport {expr} ct original packets 1-12 queue num {qnum} bypass");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static List<string> MergePortRanges(string raw)
    {
        var intervals = new List<(int Start, int End)>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(token[..dash], out var s) && int.TryParse(token[(dash + 1)..], out var e))
                    intervals.Add((Math.Clamp(s, 1, 65535), Math.Clamp(e, 1, 65535)));
            }
            else if (int.TryParse(token, out var single))
            {
                var p = Math.Clamp(single, 1, 65535);
                intervals.Add((p, p));
            }
        }

        if (intervals.Count == 0)
            return [];

        intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var merged = new List<(int Start, int End)> { intervals[0] };
        for (var i = 1; i < intervals.Count; i++)
        {
            var prev = merged[^1];
            var cur = intervals[i];
            if (cur.Start <= prev.End + 1)
                merged[^1] = (prev.Start, Math.Max(prev.End, cur.End));
            else
                merged.Add(cur);
        }

        return merged.Select(x => x.Start == x.End ? $"{x.Start}" : $"{x.Start}-{x.End}").ToList();
    }

    private static bool TryParseStartArgs(
        string[] args,
        out string nfqwsPath,
        out string tcpPorts,
        out string udpPorts,
        out int qnum,
        out string strategyName,
        out List<string> nfqwsArgs,
        out string error)
    {
        nfqwsPath = "";
        tcpPorts = "80,443";
        udpPorts = "443";
        qnum = 200;
        strategyName = "custom";
        nfqwsArgs = [];
        error = "";

        for (var i = 0; i < args.Length; i++)
        {
            var cur = args[i];
            if (cur == "--")
            {
                nfqwsArgs.AddRange(args.Skip(i + 1));
                break;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Не указано значение для {cur}";
                return false;
            }

            switch (cur)
            {
                case "--nfqws":
                    nfqwsPath = args[++i];
                    break;
                case "--tcp":
                    tcpPorts = args[++i];
                    break;
                case "--udp":
                    udpPorts = args[++i];
                    break;
                case "--qnum":
                    if (!int.TryParse(args[++i], out qnum))
                    {
                        error = "Некорректный --qnum";
                        return false;
                    }
                    break;
                case "--strategy":
                    strategyName = args[++i];
                    break;
                default:
                    error = $"Неизвестный флаг: {cur}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(nfqwsPath))
        {
            error = "Не передан --nfqws";
            return false;
        }

        return true;
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    private static string? FindBinary(string name)
    {
        var candidates = new[] { $"/usr/sbin/{name}", $"/sbin/{name}", $"/usr/bin/{name}", $"/bin/{name}", $"/usr/local/bin/{name}" };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SystemdEscape(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, IEnumerable<string> args)
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

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, ((await outTask) + Environment.NewLine + (await errTask)).Trim());
    }
}