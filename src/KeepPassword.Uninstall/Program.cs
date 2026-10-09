using Avalonia;
using KeepPassword.Core.Install;

namespace KeepPassword.Uninstall;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (TryHandleCli(args))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static bool TryHandleCli(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (args.Any(item => item is "--quiet" or "/S" or "/silent"))
        {
            var installRoot = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            InstallOperations.Uninstall(installRoot, deleteCache: false);
            return true;
        }

        var runIndex = Array.FindIndex(args, item => item == "--run-uninstall");
        if (runIndex < 0 || runIndex + 1 >= args.Length)
        {
            return false;
        }

        var target = args[runIndex + 1].Trim('"');
        var deleteCache = args.Any(item => item == "--delete-cache");
        // 稍等主界面进程退出，便于删掉安装目录里的 Uninstall.exe
        Thread.Sleep(800);
        InstallOperations.Uninstall(target, deleteCache);
        return true;
    }
}
