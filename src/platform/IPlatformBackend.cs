using NetZapret.Core.Models;
using NetZapret.Core.Services;

namespace NetZapret.Platform;

public enum DiagnosticSeverity
{
    Ok,
    Warning,
    Error
}

public sealed record DiagnosticItem(
    string Title,
    string Message,
    DiagnosticSeverity Severity,
    string? ActionId = null,
    string? ActionLabel = null
);

public sealed record BypassStatus(
    bool IsBypassRunning,
    bool IsServiceInstalled,
    bool IsServiceRunning,
    string? ActiveStrategyId,
    string? ActiveStrategyName,
    string? DriverStatus
);

public interface IPlatformBackend
{
    bool IsElevated { get; }
    string HostsFilePath { get; }

    Task<BypassStatus> GetStatusAsync(CancellationToken ct = default);

    Task<(bool Success, string Message)> StartBypassAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default);

    Task StopBypassAsync(CancellationToken ct = default);

    Task<(bool Success, string Message)> InstallServiceAsync(
        StrategyManifest manifest,
        StrategyDefinition strategy,
        LaunchSettings settings,
        CancellationToken ct = default);

    Task<(bool Success, string Message)> RemoveServiceAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(string assetsPath, CancellationToken ct = default);

    Task<(bool Success, string Message)> ExecuteDiagnosticActionAsync(string actionId, string assetsPath, CancellationToken ct = default);

    Task<(bool Success, string Message)> UpdateHostsAsync(string remoteHostsContent, CancellationToken ct = default);

    Task<(bool Success, string Message)> ClearDiscordCacheAsync(CancellationToken ct = default);
}