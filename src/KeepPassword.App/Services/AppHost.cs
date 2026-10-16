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
    private readonly AutofillGate _gate = new();
    private readonly AutoLockMonitor _autoLock;
    private readonly InstanceActivation _activation;
    private VaultSession? _session;
    private MainWindow? _main;
    private IDisposable? _hotkey;
    private bool _hotkeyPaused;
    private bool _hotkeyBusy;
    private bool _trayReady;
    private bool _locking;
    private volatile bool _exiting;

    public AppHost(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _detector = PlatformAutofill.CreateDetector();
        _filler = PlatformAutofill.CreateFiller();
        _server = new NativeMessagingServer(HandleMessageAsync);
        _autoLock = new AutoLockMonitor(LockVault);
        _activation = new InstanceActivation();
        if (!_activation.IsPrimary)
        {
            _exiting = true;
        }
    }

    public void Start()
    {
        if (_exiting)
        {
            _activation.SignalActivate();
            _desktop.Shutdown();
            return;
        }

        _activation.StartListening(ShowFromTray);
        InstallTray();
        _server.Start();
        AppUpdateService.ExitApplication = Shutdown;
        try
        {
            ShowShell();
        }
        catch (Exception)
        {
            Shutdown();
            return;
        }

        InstallHotkey();
        _ = AppUpdateService.SilentCheckAsync();
    }

    private void InstallHotkey()
    {
        if (!OperatingSystem.IsWindows() || !_detector.IsSupported)
        {
            return;
        }

        InstallWindowsHotkey();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallWindowsHotkey()
    {
        try
        {
            var hotkey = new Platform.Windows.WindowsGlobalHotkey();
            hotkey.Pressed += () => _ = OnHotkeyAsync();
            _hotkey = hotkey;
            AutofillHotkeyPreferences.Attach(
                gesture => hotkey.Register(gesture.Win32Modifiers, gesture.VirtualKey),
                hotkey.Unregister);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _hotkey = null;
        }
    }

    private async Task OnHotkeyAsync()
    {
        if (_hotkeyPaused || _hotkeyBusy || _locking || _exiting)
        {
            return;
        }

        _hotkeyBusy = true;
        try
        {
            if (_session is not { IsUnlocked: true })
            {
                if (_main is not null)
                {
                    await Dialogs.AlertAsync(_main, "Keep Password 已锁定", "请先解锁 Keep Password，再回到目标程序的密码框按快捷键。");
                }

                return;
            }

            // UI Automation 扫树可能很慢；放到后台，避免拖死界面。
            var field = await Task.Run(() =>
            {
                try
                {
                    return _detector.DetectForeground().FirstOrDefault();
                }
                catch (Exception)
                {
                    return null;
                }
            });

            if (field is null)
            {
                if (_main is not null)
                {
                    await Dialogs.AlertAsync(_main, "没有识别到密码框",
                        $"请先点击目标程序的密码框，再按 {AutofillHotkeyPreferences.Current}。浏览器里的网页请使用浏览器扩展；自绘或未开放辅助功能的输入框无法识别，可以从保险库复制密码。");
                }

                return;
            }

            await OnWindowsFieldAsync(field);
        }
        finally
        {
            _hotkeyBusy = false;
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
        AutofillIndexStore.Write(session.VaultPath, session.Entries);
        _main?.ShowVault(session);
        _autoLock.Start();
    }

    public void Dispose()
    {
        _autoLock.Dispose();
        _hotkey?.Dispose();
        _server.Dispose();
        _session?.Dispose();
        _activation.Dispose();
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is null || _main.AllowClose || _exiting)
        {
            return;
        }

        if (e.CloseReason == WindowCloseReason.OSShutdown)
        {
            Dispatcher.UIThread.Post(Shutdown);
            return;
        }

        e.Cancel = true;
        if (_trayReady)
        {
            _main.Hide();
            return;
        }

        _main.WindowState = WindowState.Minimized;
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

    private void PauseWatcher(bool paused) => _hotkeyPaused = paused;

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

            using var iconStream = AssetLoader.Open(new Uri("avares://KeepPassword/Assets/app.ico"));
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
        _hotkey?.Dispose();
        _hotkey = null;
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

        if (request.Operation == "discover-candidates")
        {
            if (_session is { IsUnlocked: true } unlocked)
            {
                return AutofillProtocol.Serialize(AutofillCoordinator.DiscoverCandidates(unlocked, request));
            }

            var cached = AutofillIndexStore.Match(_location.VaultFile, request, out var available);
            if (!available)
            {
                return AutofillProtocol.Serialize(AutofillResponse.Locked());
            }

            return AutofillProtocol.Serialize(cached.Count == 0
                ? AutofillResponse.NoMatch()
                : AutofillResponse.CandidatesList(cached));
        }

        if (request.Operation == "fill-candidate")
        {
            if (_session is { IsUnlocked: true } unlocked)
            {
                return AutofillProtocol.Serialize(AutofillCoordinator.FillCandidate(unlocked, request));
            }

            var vaultPath = _location.VaultFile;
            var lockedResponse = await Task.Run(
                () => AutofillCoordinator.FillCandidateFromLockedVault(_store, vaultPath, request), cancellationToken);
            return AutofillProtocol.Serialize(lockedResponse);
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
            TargetProcess = field.ProcessName,
            PasswordOnly = !field.HasUsernameField
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

                if (!_filler.TryFill(field, response.Username, response.Password))
                {
                    const string message = "无法填入原密码框。目标窗口可能已关闭、焦点已改变，或输入控件不支持自动填充。请重新聚焦密码框后重试，也可以从保险库复制密码。";
                    if (_main is not null)
                    {
                        await Dialogs.AlertAsync(_main, "自动填充失败", message);
                    }

                    return AutofillResponse.Error(message);
                }
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
