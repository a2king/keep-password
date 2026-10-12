using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KeepPassword.App.Services;

namespace KeepPassword.App;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize()
    {
        Image.SourceProperty.Changed.AddClassHandler<Image>((image, _) =>
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality));
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ThemeManager.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _host = new AppHost(desktop);
            _host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
