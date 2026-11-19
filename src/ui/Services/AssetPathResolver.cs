using System.Diagnostics;
using NetZapret.Core.Services;

namespace NetZapret.UI.Services;

public static class AssetPathResolver
{
    public static string ResolveAssetsPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var localAssets = Path.Combine(exeDir, "assets");
        if (Directory.Exists(localAssets) && File.Exists(Path.Combine(localAssets, "strategies.json")))
            return Path.GetFullPath(localAssets);

        var dir = new DirectoryInfo(exeDir);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var devAssets = Path.Combine(dir.FullName, "assets");
            if (Directory.Exists(devAssets) && File.Exists(Path.Combine(devAssets, "strategies.json")))
                return Path.GetFullPath(devAssets);
            dir = dir.Parent;
        }

        string standaloneDir;
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(common) && CanWriteToDirectory(common))
            {
                standaloneDir = Path.Combine(common, "NetZapret", "assets");
            }
            else
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                standaloneDir = Path.Combine(local, "NetZapret", "assets");
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            standaloneDir = Path.Combine(home, ".local", "share", "netzapret", "assets");
        }

        EmbeddedAssetsExtractor.ExtractAssetsIfNeeded(standaloneDir);
        return Path.GetFullPath(standaloneDir);
    }

    private static bool CanWriteToDirectory(string dir)
    {
        try
        {
            var testDir = Path.Combine(dir, $"NetZapret_perm_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDir);
            Directory.Delete(testDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}