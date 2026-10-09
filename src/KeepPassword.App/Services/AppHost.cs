using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using KeepPassword.App.Platform;
using KeepPassword.App.Views;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Messaging;
using KeepPassword.Core.Paths;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Services;

public sealed class AppHost : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly VaultStore _store = new();
    private readonly VaultLocation _location = new();
    private readonly string _settingsFile = CacheDirectory.SettingsFile();
    private readonly IPasswordFieldDetector _detector;
    private readonly ICredentialFiller _filler;
    private readonly NativeMessagingServer _server;
    private readonly AutofillWatcher _watcher;
    private readonly Mutex _mutex;
    private readonly DispatcherTimer _autoLockTimer;
    private VaultSession? _session;
    private MainWindow? _main;
    private bool _trayReady;
    private bool _locking;
    private bool _exiting;
    private bool _autoLockPaused;
    private int _autoLockSeconds = AppSettings.DefaultAutoLockSeconds;

    public AppHost(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _detector = PlatformAutofill.CreateDetector();
        _filler = PlatformAutofill.CreateFiller();
        _server = new NativeMessagingServer(HandleMessageAsync);
        _watcher = new AutofillWatcher(_detector, OnWindowsFieldAsync);
        _autoLockTimer = new DispatcherTimer();
        _autoLockTimer.Tick += (_, _) => _ = LockAsync();
        _mutex = new Mutex(true, "KeepPassword.SingleInstance", out var created);
        if (!created)
        {
            _exiting = true;
        }
    }

    public void Start()
    {
        if (_exiting)
        {
            _desktop.Shutdown();
            return;
        }

        _autoLockSeconds = AppSettings.GetAutoLockSeconds(_settingsFile);
        InstallTray();
        _server.Start();
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await ShowUnlockAsync(softSession: null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Shutdown();
        }
    }

    public void Dispose()
    {
        _autoLockTimer.Stop();
        _watcher.Stop();
        _server.Dispose();
        _session?.Dispose();
        _mutex.Dispose();
    }

    private async Task ShowUnlockAsync(VaultSession? softSession)
    {
        if (_exiting)
        {
            return;
        }

        StopAutoLock();
        UnlockWindow window = softSession is not null
            ? new UnlockWindow(softSession)
            : new UnlockWindow(_store, _location);
        _desktop.MainWindow = window;
        var session = await window.WaitAsync();
        if (session is null)
        {
            if (softSession is not null)
            {
                softSession.Lock();
                if (ReferenceEquals(_session, softSession))
                {
                    _session = null;
                }
            }

            Shutdown();
            return;
        }

        ShowMain(session);
    }

    private void ShowMain(VaultSession session)
    {
        _session = session;
        _main = new MainWindow(
            session,
            _detector.IsSupported,
            _location,
            PauseWatcher,
            () => _autoLockSeconds,
            ApplyAutoLockSeconds);
        _main.LockRequested += (_, _) => _ = LockAsync();
        _main.UserActivity += (_, _) => ResetAutoLock();
        _main.Closing += OnMainClosing;
        _desktop.MainWindow = _main;
        _main.Show();
        _watcher.Start();
        StartAutoLock();
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is null || _main.AllowClose || _exiting)
        {
            return;
        }

        e.Cancel = true;
        if (_trayReady)
        {
            _main.Hide();
            return;
        }

        _ = LockAsync();
    }

    private async Task LockAsync()
    {
        if (_locking || _session is null || !_session.IsUnlocked)
        {
            return;
        }

        _locking = true;
        try
        {
            StopAutoLock();
            _watcher.Stop();
            _session.SoftLock();
            if (_main is not null)
            {
                _main.PrepareClose();
                _main.Close();
                _main = null;
            }

            await ShowUnlockAsync(_session);
        }
        finally
        {
            _locking = false;
        }
    }

    private void PauseWatcher(bool paused)
    {
        _autoLockPaused = paused;
        if (paused)
        {
            _watcher.Stop();
            StopAutoLock();
            return;
        }

        if (_session is { IsUnlocked: true })
        {
            _watcher.Start();
            StartAutoLock();
        }
    }

    private void StartAutoLock()
    {
        if (_autoLockPaused || _session is not { IsUnlocked: true })
        {
            return;
        }

        _autoLockTimer.Interval = TimeSpan.FromSeconds(Math.Max(AppSettings.MinAutoLockSeconds, _autoLockSeconds));
        _autoLockTimer.Stop();
        _autoLockTimer.Start();
    }

    private void StopAutoLock() => _autoLockTimer.Stop();

    private void ResetAutoLock()
    {
        if (_autoLockPaused || _session is not { IsUnlocked: true })
        {
            return;
        }

        StartAutoLock();
    }

    public void ApplyAutoLockSeconds(int seconds)
    {
        _autoLockSeconds = AppSettings.NormalizeAutoLock(seconds);
        AppSettings.SetAutoLockSeconds(_settingsFile, _autoLockSeconds);
        ResetAutoLock();
    }

    public int AutoLockSeconds => _autoLockSeconds;

    private void InstallTray()
    {
        if (Application.Current is null)
        {
            return;
        }

        try
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示");
            show.Click += (_, _) => ShowFromTray();
            var lockItem = new NativeMenuItem("锁定");
            lockItem.Click += (_, _) => _ = LockAsync();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => Shutdown();
            menu.Items.Add(show);
            menu.Items.Add(lockItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            using var iconStream = AssetLoader.Open(new Uri("avares://KeepPassword/Assets/tray.png"));
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "Keep Password",
                Menu = menu,
                IsVisible = true
            };
            tray.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
            _trayReady = true;
        }
        catch (Exception)
        {
            _trayReady = false;
        }
    }

    private void ShowFromTray()
    {
        if (_main is { } main && _session is { IsUnlocked: true })
        {
            main.Show();
            main.Activate();
            ResetAutoLock();
            return;
        }

        _desktop.MainWindow?.Show();
        _desktop.MainWindow?.Activate();
    }

    private void Shutdown()
    {
        _exiting = true;
        StopAutoLock();
        _watcher.Stop();
        _session?.Lock();
        _session = null;
        if (_main is not null)
        {
            _main.PrepareClose();
            _main.Close();
        }

        _desktop.Shutdown();
    }

    private Task<string> HandleMessageAsync(string json, CancellationToken cancellationToken)
    {
        return Dispatcher.UIThread.InvokeAsync(() => HandleOnUiAsync(json, cancellationToken));
    }

    private async Task<string> HandleOnUiAsync(string json, CancellationToken cancellationToken)
    {
        if (!AutofillProtocol.TryParseDiscover(json, out var request))
        {
            return AutofillProtocol.Serialize(AutofillResponse.Error("无法识别的消息。"));
        }

        var response = await PromptAndFillAsync(request, field: null, cancellationToken);
        return AutofillProtocol.Serialize(response);
    }

    private async Task OnWindowsFieldAsync(DetectedPasswordField field)
    {
        if (_session is not { IsUnlocked: true })
        {
            return;
        }

        ResetAutoLock();
        var request = new AutofillRequest
        {
            Origin = AutofillOrigin.WindowsApplication,
            WindowTitle = field.WindowTitle
        };
        await PromptAndFillAsync(request, field, CancellationToken.None);
    }

    private async Task<AutofillResponse> PromptAndFillAsync(
        AutofillRequest request,
        DetectedPasswordField? field,
        CancellationToken cancellationToken)
    {
        var prompter = new WindowPrompter(_main);
        var response = await AutofillCoordinator.HandleDiscoverAsync(_session, request, prompter, cancellationToken);
        if (field is not null && response.Type == "fill" && response.Username is not null && response.Password is not null)
        {
            _filler.TryFill(field, response.Username, response.Password);
        }

        return response;
    }

    private sealed class WindowPrompter : IAutofillPrompter
    {
        private readonly Window? _owner;

        public WindowPrompter(Window? owner) => _owner = owner;

        public async Task<AutofillDecision?> PromptAsync(
            AutofillRequest request,
            IReadOnlyList<AutofillCandidate> matches,
            Func<string, bool> verifyShortKey,
            CancellationToken cancellationToken)
        {
            var window = new AutofillPromptWindow(request, matches, verifyShortKey);
            if (_owner is { IsVisible: true })
            {
                await window.ShowDialog(_owner);
            }
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                window.Activate();
                await closed.Task;
            }

            return window.Decision;
        }
    }
}
