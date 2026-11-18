using NetZapret.Core.Models;

namespace NetZapret.Core.Services;

public sealed record BuiltCommand(
    IReadOnlyList<string> Arguments,
    string TcpPortsForFirewall,
    string UdpPortsForFirewall
);

public static class CommandBuilder
{
    public static BuiltCommand Build(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        bool isWindows,
        bool useRelativePaths = false)
    {
        var d = manifest.Defaults;
        var listsDir = (useRelativePaths ? "lists" : Path.Combine(settings.AssetsPath, "lists")) + Path.DirectorySeparatorChar;
        var fakesDir = (useRelativePaths ? Path.Combine("bin", "fakes") : Path.Combine(settings.AssetsPath, "bin", "fakes")) + Path.DirectorySeparatorChar;

        var gameTcp = settings.GameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly
            ? settings.GameTcpPorts
            : "12";
        var gameUdp = settings.GameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.UdpOnly
            ? settings.GameUdpPorts
            : "12";

        var allTcpPorts = $"{d.WebPortsTcp},{d.DiscordMediaPortsTcp},{gameTcp}";
        var allUdpPorts = $"443,{d.DiscordVoicePortsUdp},{gameUdp}";

        var args = new List<string>();

        if (isWindows)
        {
            args.Add($"--wf-tcp={allTcpPorts}");
            args.Add($"--wf-udp={allUdpPorts}");
        }
        else
        {
            args.Add($"--qnum={settings.LinuxQueueNum}");
        }

        string ExpandFakes(string raw) => raw.Replace("{FAKES}", fakesDir, StringComparison.Ordinal);

        string[] CommonExcludes() =>
        [
            $"--hostlist-exclude={listsDir}list-exclude.txt",
            $"--hostlist-exclude={listsDir}list-exclude-user.txt",
            $"--ipset-exclude={listsDir}ipset-exclude.txt",
            $"--ipset-exclude={listsDir}ipset-exclude-user.txt"
        ];

        var firstBlock = true;
        void AddBlock(IEnumerable<string> tokens)
        {
            if (!firstBlock)
                args.Add("--new");
            firstBlock = false;
            args.AddRange(tokens);
        }

        var qUseL7 = strategy.Quic?.UseL7 ?? d.Quic.UseL7 ?? false;
        var qRepeats = strategy.Quic?.Repeats ?? d.Quic.Repeats ?? 6;
        var qFake = strategy.Quic?.FakeQuic ?? d.Quic.FakeQuic ?? "quic_initial_www_google_com.bin";

        var quicBlock = new List<string>
        {
            qUseL7 ? "--filter-l7=quic" : "--filter-udp=443",
            $"--hostlist={listsDir}list-general.txt",
            $"--hostlist={listsDir}list-general-user.txt"
        };
        quicBlock.AddRange(CommonExcludes());
        quicBlock.AddRange([
            "--dpi-desync=fake",
            $"--dpi-desync-repeats={qRepeats}",
            $"--dpi-desync-fake-quic={fakesDir}{qFake}"
        ]);
        AddBlock(quicBlock);

        var dvL7 = strategy.DiscordVoice?.L7 ?? d.DiscordVoice.L7 ?? "discord,stun";
        var dvRepeats = strategy.DiscordVoice?.Repeats ?? d.DiscordVoice.Repeats ?? 6;
        var dvAny = strategy.DiscordVoice?.AnyProtocol ?? d.DiscordVoice.AnyProtocol ?? false;
        var dvCutoff = strategy.DiscordVoice?.Cutoff ?? d.DiscordVoice.Cutoff;
        var dvDiscordFakes = strategy.DiscordVoice?.FakesDiscord ?? d.DiscordVoice.FakesDiscord ?? ["ACTIVE_DISCORD_UDP.bin"];
        var dvStunFakes = strategy.DiscordVoice?.FakesStun ?? d.DiscordVoice.FakesStun ?? ["ACTIVE_DISCORD_UDP.bin"];
        var dvUnknownFakes = strategy.DiscordVoice?.FakesUnknownUdp ?? d.DiscordVoice.FakesUnknownUdp;

        var dvBlock = new List<string>
        {
            $"--filter-udp={d.DiscordVoicePortsUdp}",
            $"--filter-l7={dvL7}",
            "--dpi-desync=fake"
        };
        if (dvAny) dvBlock.Add("--dpi-desync-any-protocol=1");
        foreach (var f in dvDiscordFakes) dvBlock.Add($"--dpi-desync-fake-discord={fakesDir}{f}");
        foreach (var f in dvStunFakes) dvBlock.Add($"--dpi-desync-fake-stun={fakesDir}{f}");
        if (dvUnknownFakes is not null)
            foreach (var f in dvUnknownFakes) dvBlock.Add($"--dpi-desync-fake-unknown-udp={fakesDir}{f}");
        dvBlock.Add($"--dpi-desync-repeats={dvRepeats}");
        if (!string.IsNullOrEmpty(dvCutoff)) dvBlock.Add($"--dpi-desync-cutoff={dvCutoff}");
        AddBlock(dvBlock);

        if (!string.IsNullOrWhiteSpace(strategy.GlobalIpv4Mode))
        {
            var globalBlock = new List<string>
            {
                "--filter-l3=ipv4",
                $"--filter-tcp={d.WebPortsTcp},{d.DiscordMediaPortsTcp}",
                $"--ipset-exclude={listsDir}ipset-exclude.txt",
                $"--ipset-exclude={listsDir}ipset-exclude-user.txt"
            };
            globalBlock.AddRange(SplitArgs(ExpandFakes(strategy.GlobalIpv4Mode)));
            AddBlock(globalBlock);
        }
        else
        {
            var webRaw = strategy.Web ?? "";
            var discordMediaRaw = strategy.DiscordMedia ?? strategy.Google ?? webRaw;
            var googleRaw = strategy.Google ?? webRaw;

            var dmBlock = new List<string>
            {
                $"--filter-tcp={d.DiscordMediaPortsTcp}",
                "--hostlist-domains=discord.media"
            };
            dmBlock.AddRange(SplitArgs(ExpandFakes(discordMediaRaw)));
            AddBlock(dmBlock);

            var gBlock = new List<string>
            {
                "--filter-tcp=443",
                $"--hostlist={listsDir}list-google.txt"
            };
            if (!strategy.GoogleKeepIpId) gBlock.Add("--ip-id=zero");
            gBlock.AddRange(SplitArgs(ExpandFakes(googleRaw)));
            AddBlock(gBlock);

            var wBlock = new List<string>
            {
                $"--filter-tcp={d.WebPortsTcp}",
                $"--hostlist={listsDir}list-general.txt",
                $"--hostlist={listsDir}list-general-user.txt"
            };
            wBlock.AddRange(CommonExcludes());
            wBlock.AddRange(SplitArgs(ExpandFakes(webRaw)));
            AddBlock(wBlock);
        }

        var ipQuicBlock = new List<string>
        {
            "--filter-udp=443",
            $"--ipset={listsDir}ipset-all.txt"
        };
        ipQuicBlock.AddRange(CommonExcludes());
        ipQuicBlock.AddRange([
            "--dpi-desync=fake",
            $"--dpi-desync-repeats={qRepeats}",
            $"--dpi-desync-fake-quic={fakesDir}{qFake}"
        ]);
        AddBlock(ipQuicBlock);

        if (string.IsNullOrWhiteSpace(strategy.GlobalIpv4Mode))
        {
            var ipWebRaw = strategy.IpsetWeb ?? strategy.Web ?? "";
            var iwBlock = new List<string>
            {
                $"--filter-tcp={d.IpsetPortsTcp}",
                $"--ipset={listsDir}ipset-all.txt"
            };
            iwBlock.AddRange(CommonExcludes());
            if (strategy.IpsetWebExcludeDomains is { Count: > 0 })
                iwBlock.Add($"--hostlist-exclude-domains={string.Join(',', strategy.IpsetWebExcludeDomains)}");
            iwBlock.AddRange(SplitArgs(ExpandFakes(ipWebRaw)));
            AddBlock(iwBlock);
        }

        var gameTcpTokens = !string.IsNullOrWhiteSpace(strategy.GameTcp)
            ? SplitArgs(ExpandFakes(strategy.GameTcp))
            : BuildGameTcpFromWeb(strategy.Web ?? "", strategy.GameTcpCutoff ?? "n3", fakesDir);

        var gtBlock = new List<string>
        {
            $"--filter-tcp={gameTcp}",
            $"--ipset={listsDir}ipset-all.txt",
            $"--ipset-exclude={listsDir}ipset-exclude.txt",
            $"--ipset-exclude={listsDir}ipset-exclude-user.txt"
        };
        gtBlock.AddRange(gameTcpTokens);
        AddBlock(gtBlock);

        var guRepeats = strategy.GameUdp?.Repeats ?? d.GameUdp.Repeats ?? 12;
        var guCutoff = strategy.GameUdp?.Cutoff ?? d.GameUdp.Cutoff ?? "n2";
        var guFakes = strategy.GameUdp?.FakeUnknownUdp ?? d.GameUdp.FakeUnknownUdp ?? ["ACTIVE_GAME_UDP.bin"];

        var guBlock = new List<string>
        {
            $"--filter-udp={gameUdp}",
            $"--ipset={listsDir}ipset-all.txt",
            $"--ipset-exclude={listsDir}ipset-exclude.txt",
            $"--ipset-exclude={listsDir}ipset-exclude-user.txt",
            "--dpi-desync=fake",
            $"--dpi-desync-repeats={guRepeats}",
            "--dpi-desync-any-protocol=1"
        };
        foreach (var f in guFakes) guBlock.Add($"--dpi-desync-fake-unknown-udp={fakesDir}{f}");
        guBlock.Add($"--dpi-desync-cutoff={guCutoff}");
        AddBlock(guBlock);

        return new BuiltCommand(args, allTcpPorts, allUdpPorts);
    }

    private static IEnumerable<string> BuildGameTcpFromWeb(string webRaw, string cutoff, string fakesDir)
    {
        var tokens = SplitArgs(webRaw.Replace("{FAKES}", fakesDir, StringComparison.Ordinal)).ToList();
        var result = new List<string>();
        var unknownAdded = false;

        foreach (var t in tokens)
        {
            result.Add(t);
            if (t.StartsWith("--dpi-desync=", StringComparison.Ordinal))
            {
                result.Add("--dpi-desync-any-protocol=1");
                result.Add($"--dpi-desync-cutoff={cutoff}");
            }
            else if (t.StartsWith("--dpi-desync-fake-tls=", StringComparison.Ordinal) && !unknownAdded)
            {
                result.Add(t.Replace("--dpi-desync-fake-tls=", "--dpi-desync-fake-unknown=", StringComparison.Ordinal));
                unknownAdded = true;
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitArgs(string input) =>
        input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}