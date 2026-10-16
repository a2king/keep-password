using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.Core.Install;
using KeepPassword.Core.Update;

namespace KeepPassword.Setup;

public partial class MainWindow : Window
{
    private readonly object _progressLock = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly StringBuilder _logText = new();
    private string? _installRoot;
    private string? _launchAfterClose;
    private bool _installSucceeded;
    private DispatcherTimer? _flushTimer;
    private double _percent;
    private string _stage = "准备中";

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
        var reinstall = existing.Version == InstallConstants.Version;
        WelcomeTitle.Text = reinstall ? "重新安装 Keep Password" : "更新 Keep Password";
        WelcomeText.Text = reinstall
            ? $"检测到已安装{version}，将重新安装到原安装目录。保险库和缓存不会被删除。"
            : $"检测到已安装{version}，将更新到 v{InstallConstants.Version} 并覆盖原安装目录。保险库和缓存不会被删除。";
        InstallButton.Content = reinstall ? "重新安装" : "立即更新";
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
        var progress = new InstallReporter(this);
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => FlushProgress());
        _flushTimer.Start();

        try
        {
            await Task.Run(() =>
            {
                progress.Report(new InstallProgress(0, "正在校验安装包…", "读取并校验安装包完整性"));
                using var payload = OpenVerifiedPayload(progress);
                progress.Report(new InstallProgress(InstallOperations.ExtractStart, "正在解压安装文件…", "安装包校验通过"));
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

            StopFlush();
            _installSucceeded = true;
            var exe = Path.Combine(target, InstallConstants.AppExecutableName());
            _launchAfterClose = options.LaunchAfterInstall && File.Exists(exe) ? exe : null;
            FinishTitle.Text = "安装完成";
            FinishText.Text = "Keep Password 已安装到：" + target +
                              (_launchAfterClose is not null ? "\n点击「完成」关闭安装程序后将自动启动。" : "\n可从开始菜单或桌面快捷方式打开。");
            FinishError.IsVisible = false;
            FinishButton.Content = "完成";
            ShowPage(finish: true);
            StepTitle.Text = "第 3 步 / 共 3 步 · 完成";
        }
        catch (Exception ex)
        {
            StopFlush();
            _installSucceeded = false;
            _launchAfterClose = null;
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_installSucceeded)
        {
            ScheduleCleanupUpdateCache();
        }

        if (_launchAfterClose is not { } exe)
        {
            return;
        }

        _launchAfterClose = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
        }
    }

    private void ScheduleCleanupUpdateCache()
    {
        string? processPath = null;
        try
        {
            processPath = Environment.ProcessPath;
        }
        catch (Exception)
        {
        }

        if (processPath is not null && UpdatePaths.IsUnderUpdateCache(processPath))
        {
            InstallOperations.ScheduleDeleteFile(processPath);
            var sha = processPath + ".sha256";
            if (File.Exists(sha))
            {
                InstallOperations.ScheduleDeleteFile(sha);
            }

            return;
        }

        var install = _installRoot ?? InstallOperations.FindExistingInstall()?.Directory;
        if (install is not null)
        {
            UpdatePaths.CleanupCache(install);
        }
    }

    private void StopFlush()
    {
        _flushTimer?.Stop();
        _flushTimer = null;
        FlushProgress();
    }

    private void FlushProgress()
    {
        double percent;
        string stage;
        lock (_progressLock)
        {
            percent = _percent;
            stage = _stage;
        }

        ProgressBar.Value = percent;
        PercentText.Text = (int)percent + "%";
        ProgressText.Text = stage;

        var appended = false;
        while (_pendingLogs.TryDequeue(out var line))
        {
            if (_logText.Length > 0)
            {
                _logText.Append('\n');
            }

            _logText.Append(line);
            appended = true;
        }

        if (appended)
        {
            LogText.Text = _logText.ToString();
            LogScroller.ScrollToEnd();
        }
    }

    private sealed class InstallReporter(MainWindow owner) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value)
        {
            lock (owner._progressLock)
            {
                owner._percent = Math.Max(owner._percent, value.Percent);
                owner._stage = value.Message;
            }

            if (!string.IsNullOrEmpty(value.Log))
            {
                owner._pendingLogs.Enqueue(value.Log);
            }
        }
    }

    private static Stream OpenVerifiedPayload(IProgress<InstallProgress> progress)
    {
        var payload = OpenPayload();
        var expected = ReadExpectedHash();
        if (string.IsNullOrWhiteSpace(expected))
        {
            payload.Dispose();
            throw new InvalidDataException("安装包缺少完整性校验值。");
        }

        var length = payload.CanSeek ? payload.Length : 0;
        var buffer = new MemoryStream(length > 0 ? (int)length : 0);
        var chunk = new byte[1024 * 1024];
        int read;
        while ((read = payload.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (length > 0)
            {
                progress.Report(new InstallProgress(3.0 * buffer.Length / length, "正在校验安装包…"));
            }
        }

        payload.Dispose();
        buffer.Position = 0;
        progress.Report(new InstallProgress(3, "正在校验安装包…", $"读取安装包 {buffer.Length / 1024.0 / 1024.0:0.0} MB，计算 SHA-256"));
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
