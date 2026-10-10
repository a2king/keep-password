using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using KeepPassword.App.Platform;
using KeepPassword.App.Views;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Messaging;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Services;

public sealed class AppHost : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly VaultStore _store = new();
    private readonly VaultLocation _location = new();
    private readonly IPasswordFieldDetector _detector;
    private readonly ICredentialFiller _filler;
    private readonly NativeMessagingServer _server;
    private readonly AutofillWatcher _watcher;
    private readonly AutofillGate _gate = new();
    private readonly AutoLockMonitor _autoLock;
    private readonly Mutex _mutex;
    private VaultSession? _session;
    private MainWindow? _main;
    private bool _trayReady;
    private bool _locking;
    private bool _exiting;

    public AppHost(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _detector = PlatformAutofill.CreateDetector();
        _filler = PlatformAutofill.CreateFiller();
        _server = new NativeMessagingServer(HandleMessageAsync);
        _watcher = new AutofillWatcher(_detector, OnWindowsFieldAsync);
        _autoLock = new AutoLockMonitor(LockVault);
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

        InstallTray();
        _server.Start();
        try
        {
            ShowShell();
        }
        catch (Exception)
        {
            Shutdown();
        }
    }

    private void ShowShell()
    {
        _main = new MainWindow(_store, _location, _detector.IsSupported, PauseWatcher);
        _main.Unlocked += BeginSession;
        _main.LockRequested += (_, _) => LockVault();
        _main.Closing += OnMainClosing;
        _desktop.MainWindow = _main;
        _main.Show();
    }

    private void BeginSession(VaultSession session)
    {
        _session = session;
        _main?.ShowVault(session);
        _autoLock.Start();
        _watcher.Start();
    }

    public void Dispose()
    {
        _autoLock.Dispose();
        _watcher.Stop();
        _server.Dispose();
        _session?.Dispose();
        _mutex.Dispose();
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is null || _main.AllowClose || _exiting)
        {
            return;
        }

        e.Cancel = true;
        if (_session is { IsUnlocked: true })
        {
            if (_trayReady)
            {
                _main.Hide();
                return;
            }

            LockVault();
            return;
        }

        Dispatcher.UIThread.Post(Shutdown);
    }

    private void LockVault()
    {
        if (_locking || _session is null)
        {
            return;
        }

        _locking = true;
        var session = _session;
        try
        {
            _autoLock.Stop();
            _watcher.Stop();
            if (_main is not null)
            {
                foreach (var owned in _main.OwnedWindows.ToArray())
                {
                    owned.Close();
                }

                _main.ShowUnlockScreen();
            }
        }
        finally
        {
            session.Lock();
            _session = null;
            _locking = false;
        }
    }

    private void PauseWatcher(bool paused)
    {
        if (paused)
        {
            _watcher.Stop();
            return;
        }

        if (_session is { IsUnlocked: true })
        {
            _watcher.Start();
        }
    }

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
            lockItem.Click += (_, _) => LockVault();
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
        if (_main is null)
        {
            return;
        }

        _main.BringToFront();
    }

    private void Shutdown()
    {
        _exiting = true;
        _autoLock.Stop();
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

        if (field.IsElevated || !AutofillPolicy.AllowAutoType(field.IsElevated))
        {
            if (_main is not null)
            {
                await Dialogs.AlertAsync(_main, "无法自动输入", "该窗口以更高权限运行。Keep Password 不会提权，也不会向这个窗口输入密码。");
            }

            return;
        }

        var request = new AutofillRequest
        {
            Origin = AutofillOrigin.WindowsApplication,
            WindowTitle = field.WindowTitle,
            TargetProcess = field.ProcessName
        };
        await PromptAndFillAsync(request, field, CancellationToken.None);
    }

    private async Task<AutofillResponse> PromptAndFillAsync(
        AutofillRequest request,
        DetectedPasswordField? field,
        CancellationToken cancellationToken)
    {
        var key = request.Url ?? request.WindowTitle ?? request.TargetProcess ?? "";
        if (!_gate.TryEnter(key, out var lease) || lease is null)
        {
            return AutofillResponse.Error("已有填充确认正在进行。");
        }

        using (lease)
        {
            var prompter = new WindowPrompter(_main);
            var response = await AutofillCoordinator.HandleDiscoverAsync(_session, request, prompter, cancellationToken);
            if (field is not null && response.Type == "fill" && response.Username is not null && response.Password is not null)
            {
                if (!AutofillPolicy.AllowAutoType(field.IsElevated))
                {
                    return AutofillResponse.Error("目标窗口权限更高，已取消输入。");
                }

                _filler.TryFill(field, response.Username, response.Password);
            }

            return response;
        }
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
