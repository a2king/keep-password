using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace KeepPassword.Setup;

public partial class App : Application
{
    public override void Initialize()
    {
        Image.SourceProperty.Changed.AddClassHandler<Image>((image, _) =>
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality));
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
