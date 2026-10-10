using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.Core.Install;

namespace KeepPassword.Setup;

public partial class MainWindow : Window
{
    private string? _installRoot;

    public MainWindow()
    {
        InitializeComponent();
        StepTitle.Text = "第 1 步 / 共 3 步 · 选项";
        var existing = FindExistingInstall();
        if (existing is null)
        {
            PathBox.Text = InstallConstants.DefaultInstallDirectory();
            return;
        }

        PathBox.Text = existing.Directory;
        StartMenuBox.IsChecked = existing.HasStartMenuShortcut;
        DesktopBox.IsChecked = existing.HasDesktopShortcut;
        var version = existing.Version is null ? "" : " v" + existing.Version;
        WelcomeTitle.Text = "更新 Keep Password";
        WelcomeText.Text = $"检测到已安装{version}，将更新到 v{InstallConstants.Version} 并覆盖原安装目录。保险库和缓存不会被删除。";
        InstallButton.Content = "立即更新";
    }

    private static ExistingInstall? FindExistingInstall()
    {
        try
        {
            return InstallOperations.FindExistingInstall();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnFinish(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            PathBox.Text = Path.Combine(path, "KeepPassword");
        }
    }

    private async void OnStartInstall(object? sender, RoutedEventArgs e)
    {
        WelcomeError.IsVisible = false;
        var target = (PathBox.Text ?? "").Trim();
        if (target.Length == 0)
        {
            WelcomeError.Text = "请填写安装目录。";
            WelcomeError.IsVisible = true;
            return;
        }

        _installRoot = target;
        var options = new InstallOptions
        {
            CreateStartMenuShortcut = StartMenuBox.IsChecked == true,
            CreateDesktopShortcut = DesktopBox.IsChecked == true,
            LaunchAfterInstall = LaunchBox.IsChecked == true
        };

        ShowPage(progress: true);
        StepTitle.Text = "第 2 步 / 共 3 步 · 安装";
        SetProgress(1, "正在读取安装包…");

        try
        {
            var progress = new Progress<InstallProgress>(item =>
            {
                Dispatcher.UIThread.Post(() => SetProgress(item.Percent, item.Message));
            });

            await Task.Run(() =>
            {
                using var payload = OpenVerifiedPayload();
                var extract = Path.Combine(Path.GetTempPath(), "keep-password-setup-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(extract);
                try
                {
                    InstallOperations.ExtractZip(payload, extract, progress);
                    InstallOperations.InstallFromDirectory(extract, target, options, progress);
                }
                finally
                {
                    try
                    {
                        Directory.Delete(extract, recursive: true);
                    }
                    catch (Exception)
                    {
                    }
                }
            });

            if (options.LaunchAfterInstall)
            {
                var exe = Path.Combine(target, InstallConstants.AppExecutableName());
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = target,
                        UseShellExecute = true
                    });
                }
            }

            FinishTitle.Text = "安装完成";
            FinishText.Text = "Keep Password 已安装到：" + target +
                              (options.LaunchAfterInstall ? "\n程序正在启动。" : "\n可从开始菜单或桌面快捷方式打开。");
            FinishError.IsVisible = false;
            FinishButton.Content = "完成";
            ShowPage(finish: true);
            StepTitle.Text = "第 3 步 / 共 3 步 · 完成";
        }
        catch (Exception ex)
        {
            FinishTitle.Text = "安装失败";
            FinishText.Text = "安装未能完成。请关闭占用程序后重试，或更换安装目录。";
            FinishError.Text = ex.Message;
            FinishError.IsVisible = true;
            FinishButton.Content = "关闭";
            ShowPage(finish: true);
            StepTitle.Text = "安装失败";
        }
    }

    private void ShowPage(bool progress = false, bool finish = false)
    {
        WelcomePage.IsVisible = !progress && !finish;
        ProgressPage.IsVisible = progress;
        FinishPage.IsVisible = finish;
    }

    private void SetProgress(double percent, string message)
    {
        ProgressBar.Value = percent;
        PercentText.Text = ((int)percent) + "%";
        ProgressText.Text = message;
    }

    private static Stream OpenVerifiedPayload()
    {
        var payload = OpenPayload();
        var expected = ReadExpectedHash();
        if (string.IsNullOrWhiteSpace(expected))
        {
            payload.Dispose();
            throw new InvalidDataException("安装包缺少完整性校验值。");
        }

        var buffer = new MemoryStream();
        payload.CopyTo(buffer);
        payload.Dispose();
        buffer.Position = 0;
        PayloadIntegrity.Verify(buffer, expected);
        buffer.Position = 0;
        return buffer;
    }

    private static Stream OpenPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("KeepPassword.Setup.payload.zip");
        if (stream is not null)
        {
            return stream;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "payload.zip");
        if (File.Exists(beside))
        {
            return File.OpenRead(beside);
        }

        throw new FileNotFoundException("安装包缺少 payload.zip。请使用官方发布的安装程序。");
    }

    private static string? ReadExpectedHash()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream("KeepPassword.Setup.payload.sha256");
        if (resource is not null)
        {
            using var reader = new StreamReader(resource);
            return reader.ReadToEnd();
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "payload.sha256");
        return File.Exists(beside) ? File.ReadAllText(beside) : null;
    }
}
