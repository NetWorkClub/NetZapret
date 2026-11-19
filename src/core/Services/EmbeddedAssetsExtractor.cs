using System.Diagnostics;
using System.Reflection;

namespace NetZapret.Core.Services;

public static class EmbeddedAssetsExtractor
{
    private const string ResourcePrefix = "NetZapretAsset:";

    public static void ExtractAssetsIfNeeded(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            var versionFile = Path.Combine(targetDir, ".embedded_version");
            var currentVersion = UpdateService.AppVersion;

            var strategiesFile = Path.Combine(targetDir, "strategies.json");
            var winwsFile = Path.Combine(targetDir, "bin", "win", "winws.exe");
            var nfqwsFile = Path.Combine(targetDir, "bin", "linux", "nfqws");

            var mustExtract = !File.Exists(strategiesFile) ||
                              !File.Exists(versionFile) ||
                              File.ReadAllText(versionFile).Trim() != currentVersion ||
                              (OperatingSystem.IsWindows() && !File.Exists(winwsFile)) ||
                              (OperatingSystem.IsLinux() && !File.Exists(nfqwsFile));

            if (!mustExtract)
                return;

            var assembly = typeof(EmbeddedAssetsExtractor).Assembly;
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (resourceNames.Count == 0)
                return;

            foreach (var name in resourceNames)
            {
                var relative = name[ResourcePrefix.Length..].Replace('\\', '/');
                var localRelative = relative.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.Combine(targetDir, localRelative);

                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                    continue;

                using var fileStream = File.Create(destPath);
                stream.CopyTo(fileStream);

                if (OperatingSystem.IsLinux() && (destPath.EndsWith("nfqws", StringComparison.OrdinalIgnoreCase) || destPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        File.SetUnixFileMode(destPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch
                    {
                    }
                }
            }

            File.WriteAllText(versionFile, currentVersion);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error extracting embedded assets: {ex.Message}");
        }
    }
}
