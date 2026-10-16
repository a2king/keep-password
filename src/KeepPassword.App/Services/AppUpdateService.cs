using System.Diagnostics;
using KeepPassword.Core.Install;
using KeepPassword.Core.Update;

namespace KeepPassword.App.Services;

public enum AppUpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Error
}

/// <summary>GitHub 发布更新：静默检查、手动检查、下载与启动安装程序。</summary>
public static class AppUpdateService
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _checkCts;
    private static CancellationTokenSource? _downloadCts;
    private static GitHubUpdateClient? _client;

    public static AppUpdatePhase Phase { get; private set; } = AppUpdatePhase.Idle;
    public static string CurrentVersion => InstallConstants.Version;
    public static UpdateRelease? Latest { get; private set; }
    public static bool UpdateAvailable { get; private set; }
    public static string? StatusMessage { get; private set; }
    public static string? Error { get; private set; }
    public static string? LocalSetupPath { get; private set; }
    public static double DownloadProgress { get; private set; }
    public static string? InstallRoot { get; private set; }
    public static bool CanAutoInstall => !string.IsNullOrWhiteSpace(InstallRoot);

    /// <summary>由 AppHost 注入：启动安装程序后退出主进程。</summary>
    public static Action? ExitApplication { get; set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        InstallRoot = UpdatePaths.ResolveInstallRoot();
        if (InstallRoot is not null)
        {
            UpdatePaths.CleanupCache(InstallRoot);
        }
    }

    public static Task SilentCheckAsync() => CheckAsync(silent: true);

    public static async Task CheckAsync(bool silent = false)
    {
        CancellationToken token;
        lock (Gate)
        {
            _checkCts?.Cancel();
            _checkCts?.Dispose();
            _checkCts = new CancellationTokenSource();
            token = _checkCts.Token;
            if (!silent || Phase is AppUpdatePhase.Idle or AppUpdatePhase.Error or AppUpdatePhase.UpToDate)
            {
                SetState(AppUpdatePhase.Checking, clearError: !silent, message: silent ? StatusMessage : "正在检查更新…");
            }
        }

        try
        {
            var client = GetClient();
            var result = await client.CheckAsync(CurrentVersion, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }

            lock (Gate)
            {
                if (result.Error is not null)
                {
                    Latest = result.Latest;
                    UpdateAvailable = false;
                    if (silent)
                    {
                        // 静默失败不打断已有「有更新」状态
                        if (Phase is not AppUpdatePhase.Available and not AppUpdatePhase.Ready and not AppUpdatePhase.Downloading)
                        {
                            Phase = AppUpdatePhase.Idle;
                            Error = null;
                            StatusMessage = null;
                            RaiseChanged();
                        }
                    }
                    else
                    {
                        Phase = AppUpdatePhase.Error;
                        Error = result.Error;
                        StatusMessage = result.Error;
                        RaiseChanged();
                    }

                    return;
                }

                Latest = result.Latest;
                UpdateAvailable = result.UpdateAvailable;
                LocalSetupPath = null;
                DownloadProgress = 0;
                Error = null;
                if (result.UpdateAvailable && result.Latest is not null)
                {
                    Phase = AppUpdatePhase.Available;
                    StatusMessage = $"发现新版本 v{result.Latest.Version}（当前 v{CurrentVersion}）。";
                }
                else
                {
                    Phase = AppUpdatePhase.UpToDate;
                    StatusMessage = $"已是最新版本 v{CurrentVersion}。";
                }

                RaiseChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (silent)
            {
                return;
            }

            lock (Gate)
            {
                Phase = AppUpdatePhase.Error;
                Error = "检查更新失败：" + ex.Message;
                StatusMessage = Error;
                RaiseChanged();
            }
        }
    }

    public static async Task DownloadAsync(IProgress<double>? progress = null)
    {
        UpdateRelease release;
        string installRoot;
        CancellationToken token;
        lock (Gate)
        {
            if (Latest is null || !UpdateAvailable)
            {
                Phase = AppUpdatePhase.Error;
                Error = "没有可下载的版本，请先检查更新。";
                StatusMessage = Error;
                RaiseChanged();
                return;
            }

            if (InstallRoot is null)
            {
                Phase = AppUpdatePhase.Error;
                Error = "当前是便携运行，请到 GitHub Releases 手动下载安装包。";
                StatusMessage = Error;
                RaiseChanged();
                return;
            }

            release = Latest;
            installRoot = InstallRoot;
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            token = _downloadCts.Token;
            Phase = AppUpdatePhase.Downloading;
            DownloadProgress = 0;
            LocalSetupPath = null;
            Error = null;
            StatusMessage = "正在下载更新…";
            RaiseChanged();
        }

        var report = new Progress<double>(value =>
        {
            progress?.Report(value);
            lock (Gate)
            {
                DownloadProgress = value;
                StatusMessage = $"正在下载更新… {(int)(value * 100)}%";
                RaiseChanged();
            }
        });

        try
        {
            var client = GetClient();
            var downloaded = await client.DownloadAsync(release, installRoot, report, token).ConfigureAwait(false);
            lock (Gate)
            {
                LocalSetupPath = downloaded.SetupPath;
                DownloadProgress = 1;
                Phase = AppUpdatePhase.Ready;
                Error = null;
                StatusMessage = $"v{release.Version} 已下载并校验通过，可以开始安装。";
                RaiseChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (Gate)
            {
                Phase = AppUpdatePhase.Available;
                DownloadProgress = 0;
                StatusMessage = "已取消下载。";
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                Phase = AppUpdatePhase.Error;
                DownloadProgress = 0;
                LocalSetupPath = null;
                Error = ex.Message;
                StatusMessage = ex.Message;
                RaiseChanged();
            }
        }
    }

    public static void CancelDownload()
    {
        lock (Gate)
        {
            _downloadCts?.Cancel();
        }
    }

    public static string? TryStartInstaller()
    {
        string? path;
        lock (Gate)
        {
            path = LocalSetupPath;
            if (Phase != AppUpdatePhase.Ready || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "安装包不存在或尚未准备好，请重新下载。";
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)
            });
        }
        catch (Exception ex)
        {
            return "无法启动安装程序：" + ex.Message;
        }

        var exit = ExitApplication;
        if (exit is null)
        {
            return "无法退出当前程序，请手动关闭后再运行安装程序。";
        }

        exit();
        return null;
    }

    private static GitHubUpdateClient GetClient()
    {
        lock (Gate)
        {
            return _client ??= new GitHubUpdateClient();
        }
    }

    private static void SetState(AppUpdatePhase phase, bool clearError, string? message)
    {
        Phase = phase;
        if (clearError)
        {
            Error = null;
        }

        StatusMessage = message;
        RaiseChanged();
    }

    private static void RaiseChanged() => Changed?.Invoke();
}
