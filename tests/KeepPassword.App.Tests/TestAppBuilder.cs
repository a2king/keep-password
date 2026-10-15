using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(KeepPassword.App.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace KeepPassword.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<IsolatedTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class IsolatedTestApplication : global::KeepPassword.App.App
{
    // Inherit Initialize to load the production resources and control styles. Do not start
    // AppHost, native messaging, autofill, a single-instance mutex, or user preferences.
    public override void OnFrameworkInitializationCompleted()
    {
    }
}
