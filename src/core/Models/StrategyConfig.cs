using System.Text.Json.Serialization;

namespace NetZapret.Core.Models;

public sealed class StrategyManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("defaults")]
    public StrategyDefaults Defaults { get; set; } = new();

    [JsonPropertyName("strategies")]
    public List<StrategyDefinition> Strategies { get; set; } = [];
}

public sealed class StrategyDefaults
{
    [JsonPropertyName("webPortsTcp")]
    public string WebPortsTcp { get; set; } = "80,443";

    [JsonPropertyName("ipsetPortsTcp")]
    public string IpsetPortsTcp { get; set; } = "80,443,8443";

    [JsonPropertyName("discordMediaPortsTcp")]
    public string DiscordMediaPortsTcp { get; set; } = "2053,2083,2087,2096,8443";

    [JsonPropertyName("discordVoicePortsUdp")]
    public string DiscordVoicePortsUdp { get; set; } = "19294-19344,50000-50100";

    [JsonPropertyName("quic")]
    public QuicRule Quic { get; set; } = new();

    [JsonPropertyName("discordVoice")]
    public DiscordVoiceRule DiscordVoice { get; set; } = new();

    [JsonPropertyName("gameUdp")]
    public GameUdpRule GameUdp { get; set; } = new();
}

public sealed class QuicRule
{
    [JsonPropertyName("useL7")]
    public bool? UseL7 { get; set; }

    [JsonPropertyName("repeats")]
    public int? Repeats { get; set; }

    [JsonPropertyName("fakeQuic")]
    public string? FakeQuic { get; set; }
}

public sealed class DiscordVoiceRule
{
    [JsonPropertyName("l7")]
    public string? L7 { get; set; }

    [JsonPropertyName("anyProtocol")]
    public bool? AnyProtocol { get; set; }

    [JsonPropertyName("cutoff")]
    public string? Cutoff { get; set; }

    [JsonPropertyName("repeats")]
    public int? Repeats { get; set; }

    [JsonPropertyName("fakesDiscord")]
    public List<string>? FakesDiscord { get; set; }

    [JsonPropertyName("fakesStun")]
    public List<string>? FakesStun { get; set; }

    [JsonPropertyName("fakesUnknownUdp")]
    public List<string>? FakesUnknownUdp { get; set; }
}

public sealed class GameUdpRule
{
    [JsonPropertyName("repeats")]
    public int? Repeats { get; set; }

    [JsonPropertyName("cutoff")]
    public string? Cutoff { get; set; }

    [JsonPropertyName("fakeUnknownUdp")]
    public List<string>? FakeUnknownUdp { get; set; }
}

public sealed class StrategyDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("legacyName")]
    public string? LegacyName { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "general";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("recommended")]
    public bool Recommended { get; set; }

    [JsonPropertyName("advancedWarning")]
    public bool AdvancedWarning { get; set; }

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    [JsonPropertyName("quic")]
    public QuicRule? Quic { get; set; }

    [JsonPropertyName("discordVoice")]
    public DiscordVoiceRule? DiscordVoice { get; set; }

    [JsonPropertyName("discordMedia")]
    public string? DiscordMedia { get; set; }

    [JsonPropertyName("google")]
    public string? Google { get; set; }

    [JsonPropertyName("googleKeepIpId")]
    public bool GoogleKeepIpId { get; set; }

    [JsonPropertyName("web")]
    public string? Web { get; set; }

    [JsonPropertyName("ipsetWeb")]
    public string? IpsetWeb { get; set; }

    [JsonPropertyName("ipsetWebExcludeDomains")]
    public List<string>? IpsetWebExcludeDomains { get; set; }

    [JsonPropertyName("gameTcp")]
    public string? GameTcp { get; set; }

    [JsonPropertyName("gameTcpCutoff")]
    public string? GameTcpCutoff { get; set; }

    [JsonPropertyName("gameUdp")]
    public GameUdpRule? GameUdp { get; set; }

    [JsonPropertyName("globalIpv4Mode")]
    public string? GlobalIpv4Mode { get; set; }
}

public enum GameFilterMode
{
    Disabled,
    TcpAndUdp,
    TcpOnly,
    UdpOnly
}

public enum IpsetMode
{
    Loaded,
    None,
    Any
}

public sealed record LaunchSettings(
    string AssetsPath,
    GameFilterMode GameFilter = GameFilterMode.Disabled,
    string GameTcpPorts = "1024-65535",
    string GameUdpPorts = "1024-65535",
    int LinuxQueueNum = 200
);