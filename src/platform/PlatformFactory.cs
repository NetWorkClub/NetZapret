using NetZapret.Platform.Linux;
using NetZapret.Platform.Windows;

namespace NetZapret.Platform;

public interface ISessionPlatformBackend
{
    Task<(bool Success, string Message)> BeginSessionAsync(CancellationToken ct = default);
    Task EndSessionAsync(CancellationToken ct = default);
}

public static class PlatformFactory
{
    public static IPlatformBackend Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsBackend();

        if (OperatingSystem.IsLinux())
            return new LinuxBackend();

        throw new PlatformNotSupportedException("Поддерживаются только Windows и Linux.");
    }
}