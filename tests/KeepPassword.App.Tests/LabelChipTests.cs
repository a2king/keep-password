using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeepPassword.App.Services;
using KeepPassword.App.Views;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class LabelChipTests
{
    [AvaloniaFact]
    public void Palette_IsStablePerNameAndDefinedForBothThemes()
    {
        // 同名（忽略大小写和首尾空白）永远同色；结果不依赖 string.GetHashCode 的进程随机化。
        Assert.Equal(LabelPalette.IndexOf("Kubernetes"), LabelPalette.IndexOf("  kubernetes "));
        var names = new[] { "工作", "生活", "云服务", "Kubernetes", "运维", "AI", "办公", "游戏", "数据库", "SSH", "邮箱", "测试" };
        Assert.All(names, name => Assert.InRange(LabelPalette.IndexOf(name), 0, LabelPalette.Count - 1));
        Assert.True(names.Select(LabelPalette.IndexOf).Distinct().Count() >= 5, "常见标签应分散到多种颜色");
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            for (var i = 0; i < LabelPalette.Count; i++)
            {
                foreach (var key in new[] { $"Label.{i}.Bg", $"Label.{i}.Fg", $"Label.{i}.Border" })
                {
                    Assert.True(Avalonia.Application.Current!.TryGetResource(key, theme, out var value) && value is Avalonia.Media.ISolidColorBrush,
                        $"{theme} 缺少 {key}");
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task SpaceChips_AddWithEnterAndRenameInlineWithEnterOrEscape()
    {
        var folder = Path.Combine(Path.GetTempPath(), "keep-password-label-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var location = new VaultLocation(Path.Combine(folder, "settings.json"), folder);
        using var session = new VaultStore(KdfProfile.Fast).CreateSession(location.VaultFile, "test", "master-password", "short-key");
        session.AddSpace("工作");
        session.Save();
        var settings = new SettingsView();
        var window = new Window { Content = settings, Width = 1000, Height = 900 };
        window.Show();
        try
        {
            settings.Show(session, location, null);
            await Task.Delay(30); window.UpdateLayout();
            var list = settings.FindControl<WrapPanel>("SpaceList")!;
            var add = settings.FindControl<TextBox>("NewSpaceBox")!;
            string[] Chips() => list.Children.OfType<Border>()
                .Select(chip => (chip.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("label-chip-name")).Content as TextBlock)?.Text ?? "").ToArray();
            Assert.Equal(["工作"], Chips());
            Assert.Same(add, list.Children[^1]);

            // 末尾输入框回车即新增，新增后输入框仍是最后一个、内容清空。
            add.Focus();
            window.KeyTextInput("生活");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.UpdateLayout();
            Assert.Contains("生活", session.Spaces);
            Assert.Contains("生活", Chips());
            Assert.Same(add, list.Children[^1]);
            Assert.True(string.IsNullOrEmpty(add.Text));

            // 点名称进入改名，Esc 取消：名称不变。
            var chip = list.Children.OfType<Border>().First();
            var title = chip.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("label-chip-name"));
            title.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = chip.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(editor.IsVisible);
            editor.Text = "不会保存";
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Contains("工作", session.Spaces);
            Assert.DoesNotContain("不会保存", session.Spaces);

            // 再次改名，回车保存：旧名消失、新名出现。
            title.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text = "公司";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.UpdateLayout();
            Assert.Contains("公司", session.Spaces);
            Assert.DoesNotContain("工作", session.Spaces);
            Assert.Contains("公司", Chips());
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }
}
