using System.Reflection;
using Avalonia;
using KeepPassword.Core.Runtime;

namespace KeepPassword.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LibProbe.Attach(typeof(AppBuilder).Assembly);
        TryAttachSkiaNative();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void TryAttachSkiaNative()
    {
        try
        {
            var skia = Assembly.Load("Avalonia.Skia");
            LibProbe.Attach(skia);
        }
        catch (Exception)
        {
        }
    }
}
