using Avalonia;
using KeepPassword.Core.Install;

namespace KeepPassword.Uninstall;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(item => item is "--quiet" or "/S" or "/silent"))
        {
            var installRoot = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            InstallOperations.Uninstall(installRoot, deleteCache: false);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
