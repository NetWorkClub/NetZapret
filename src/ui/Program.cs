using Avalonia;

namespace NetZapret.UI;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[CRITICAL STARTUP ERROR] {ex}");
            Console.ResetColor();
            try
            {
                File.WriteAllText("crash.log", ex.ToString());
            }
            catch
            {
            }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
