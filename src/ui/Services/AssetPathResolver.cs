using System.Diagnostics;

namespace NetZapret.UI.Services;

public static class AssetPathResolver
{
    public static string ResolveAssetsPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "assets")
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "assets"));
            dir = dir.Parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        candidates.Add(Path.Combine(cwd, "assets"));

        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, "strategies.json")))
                return Path.GetFullPath(path);
        }

        return Path.GetFullPath(candidates[0]);
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