using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using KeepPassword.App.Services;
using KeepPassword.App.Views;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class UpdateBadgeTests
{
    [AvaloniaFact]
    public async Task SettingsBadge_SitsOnTheIconsTopRightCorner_NotTheButtonsEdge()
    {
        var folder = Path.Combine(Path.GetTempPath(), "keep-password-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new VaultStore(KdfProfile.Fast);
        var location = new VaultLocation(Path.Combine(folder, "settings.json"), folder);
        using var session = store.CreateSession(location.VaultFile, "badge test", "test master password", "test short key");
        var window = new MainWindow(store, location, platformAutofillSupported: false) { Width = 1200, Height = 760 };
        try
        {
            window.Show();
            window.ShowVault(session);
            var badge = window.FindControl<Avalonia.Controls.Shapes.Ellipse>("SettingsUpdateBadge")!;
            var nav = window.FindControl<Button>("NavSettings")!;
            badge.IsVisible = true;
            await Task.Delay(60);
            window.UpdateLayout();

            // 图标就是红点所在 Panel 的另一个孩子；红点中心应落在图标右上角附近，而不是按钮右上角。
            var icon = badge.GetVisualParent()!.GetVisualChildren().OfType<PathIcon>().Single();
            var iconBox = new Rect(icon.TranslatePoint(default, window)!.Value, icon.Bounds.Size);
            var center = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), window)!.Value;
            var navBox = new Rect(nav.TranslatePoint(default, window)!.Value, nav.Bounds.Size);

            Assert.InRange(center.X, iconBox.Right - 6, iconBox.Right + 6);
            Assert.InRange(center.Y, iconBox.Top - 6, iconBox.Top + 6);
            // 完整地落在按钮里，不会被裁切，也不会压到“设置”两个字上。
            Assert.True(navBox.Contains(new Point(center.X + badge.Bounds.Width / 2, center.Y - badge.Bounds.Height / 2)));
            // 红点不拦截鼠标：点击它等同点击设置。
            Assert.False(badge.IsHitTestVisible);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
