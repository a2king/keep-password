using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using KeepPassword.App.Views;

namespace KeepPassword.App.Tests;

public class CopyFeedbackTests
{
    [AvaloniaFact]
    public async Task RepeatedCopy_RestartsFeedbackAndRestoresTheOriginalIcon()
    {
        var original = Geometry.Parse("M0,0 L10,10");
        var icon = new PathIcon { Data = original };
        var button = new Button { Content = icon };
        var window = new Window { Content = button };
        window.Show();
        try
        {
            var first = CopyFeedback.ShowAsync(button, TimeSpan.FromSeconds(1));
            Assert.NotSame(original, icon.Data);

            var second = CopyFeedback.ShowAsync(button, TimeSpan.FromMilliseconds(60));
            await first;
            Assert.Contains("copy-success", button.Classes);
            Assert.NotSame(original, icon.Data);

            await second;
            Assert.Same(original, icon.Data);
            Assert.DoesNotContain("copy-success", button.Classes);
        }
        finally
        {
            window.Close();
        }
    }
}
