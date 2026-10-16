using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

        if (!await EnsureNoRunningAppAsync(target))
        {
            return;
        }

        ShowPage(progress: true);
        StepTitle.Text = "第 2 步 / 共 3 步 · 安装";
        _logText.Clear();
        LogText.Text = "";
        lock (_progressLock)
        {
            _percent = 0;
            _stage = "准备中";
        }

        var progress = new InstallReporter(this);
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => FlushProgress());
        _flushTimer.Start();

        string? extract = null;
        try
        {
            extract = await Task.Run(() =>
            {
                progress.Report(new InstallProgress(0, "正在校验安装包…", "读取并校验安装包完整性"));
                using var payload = OpenVerifiedPayload(progress);
                progress.Report(new InstallProgress(InstallOperations.ExtractStart, "正在解压安装文件…", "安装包校验通过"));
                var folder = Path.Combine(Path.GetTempPath(), "keep-password-setup-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(folder);
                InstallOperations.ExtractZip(payload, folder, progress);
                return folder;
            });

            const int maxLockRetries = 3;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var installTarget = target;
                    var installExtract = extract;
                    await Task.Run(() =>
                        InstallOperations.InstallFromDirectory(installExtract, installTarget, options, progress));
                    break;
                }
                catch (Exception ex) when (InstallOperations.IsFileInUseException(ex) && attempt < maxLockRetries)
                {
                    double percent;
                    lock (_progressLock)
                    {
                        percent = _percent;
                    }

                    FlushProgress();
                    progress.Report(new InstallProgress(percent, "检测到程序仍在运行…", "安装目录中的文件正被占用"));
                    FlushProgress();
                    if (!await EnsureNoRunningAppAsync(target, forcePrompt: true))
                    {
                        throw new OperationCanceledException("用户取消：未关闭正在运行的 Keep Password。", ex);
                    }

                    lock (_progressLock)
                    {
                        percent = _percent;
                    }

                    progress.Report(new InstallProgress(percent, "正在重新写入程序文件…", "已退出占用进程，继续安装"));
                }
            }

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
        catch (OperationCanceledException ex)
        {
            StopFlush();
            _installSucceeded = false;
            _launchAfterClose = null;
            FinishTitle.Text = "安装已取消";
            FinishText.Text = "安装已取消。请退出 Keep Password 后重新运行安装程序。";
            FinishError.Text = ex.Message;
            FinishError.IsVisible = true;
            FinishButton.Content = "关闭";
            ShowPage(finish: true);
            StepTitle.Text = "安装已取消";
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
        finally
        {
            if (extract is not null)
            {
                try
                {
                    Directory.Delete(extract, recursive: true);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// 若安装目录内仍有 Keep Password 在运行：弹窗询问。确认则结束进程；取消则返回 false。
    /// </summary>
    private async Task<bool> EnsureNoRunningAppAsync(string installRoot, bool forcePrompt = false)
    {
        var running = InstallOperations.FindRunningProductProcesses(installRoot);
        if (running.Count == 0)
        {
            if (!forcePrompt)
            {
                return true;
            }

            // 文件仍被占用但未识别到进程时，仍提示用户确认重试
            var retry = await ConfirmAsync(
                "文件被占用",
                "安装目录中的文件正被其它程序占用，但未识别到正在运行的 Keep Password。\n\n仍要重试安装吗？",
                "重试",
                "取消");
            return retry;
        }

        try
        {
            var names = string.Join("、", running.Select(p =>
            {
                try
                {
                    return p.ProcessName + " (PID " + p.Id + ")";
                }
                catch (Exception)
                {
                    return "Keep Password";
                }
            }).Distinct());

            var confirmed = await ConfirmAsync(
                "需要退出正在运行的程序",
                "检测到以下程序仍在运行，会占用安装目录中的文件，导致无法覆盖更新：\n\n"
                + names
                + "\n\n确认后将退出这些程序并继续安装；取消则中止安装。",
                "退出并继续",
                "取消");
            if (!confirmed)
            {
                DisposeProcesses(running);
                return false;
            }

            progressLogClose(running.Count);
            await Task.Run(() => InstallOperations.CloseRunningProductProcesses(running));
            await Task.Delay(400);
            return true;
        }
        catch (Exception)
        {
            DisposeProcesses(running);
            throw;
        }

        void progressLogClose(int count) =>
            _pendingLogs.Enqueue($"正在退出占用进程（{count} 个）…");
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
    {
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.BorderOnly
        };

        var accepted = false;
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.Parse("#4A5568")),
            Margin = new Thickness(0, 0, 0, 20)
        };
        var cancel = new Button { Content = cancelText, MinWidth = 88, Padding = new Thickness(14, 8) };
        cancel.Click += (_, _) => window.Close();
        var ok = new Button
        {
            Content = okText,
            MinWidth = 88,
            Padding = new Thickness(14, 8),
            Background = new SolidColorBrush(Color.Parse("#0B4F9C")),
            Foreground = Brushes.White
        };
        ok.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancel, ok }
        };
        window.Content = new Border
        {
            Padding = new Thickness(24),
            Background = Brushes.White,
            Child = new StackPanel { Children = { titleBlock, text, buttons } }
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                accepted = true;
                window.Close();
            }
        };

        await window.ShowDialog(this);
        return accepted;
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
