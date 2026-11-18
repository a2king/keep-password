using System.Net.Sockets;
using Avalonia.Threading;

namespace KeepPassword.App.Services;

/// <summary>
/// 单实例互斥 + 二次启动时唤起已有窗口。Windows 用命名 EventWaitHandle；
/// Linux/macOS 用不带名字的本地 Unix domain socket（命名 EventWaitHandle 不受支持）。
/// </summary>
public sealed class InstanceActivation : IDisposable
{
    private const string MutexName = "KeepPassword.SingleInstance";
    private const string WindowsEventName = "KeepPassword.Activate";
    private static readonly byte[] ActivatePayload = "activate"u8.ToArray();

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _windowsEvent;
    private readonly string? _socketPath;
    private Socket? _listener;
    private CancellationTokenSource? _listenCts;
    private volatile bool _disposed;

    public InstanceActivation()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        IsPrimary = created;
        if (OperatingSystem.IsWindows())
        {
            _windowsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsEventName);
        }
        else
        {
            _socketPath = UnixSocketPath();
        }
    }

    public bool IsPrimary { get; }

    /// <summary>
    /// 每个用户自己的 socket 路径。原来放在共享的 /tmp 下：Linux 多用户时，后启动的用户删不掉别人的 socket
    /// （UnauthorizedAccessException 未被捕获会直接崩溃），也绑定不上。优先用 XDG_RUNTIME_DIR（只有本人可访问），
    /// 否则在临时目录建一个仅本人可访问的子目录。macOS 的 socket 路径上限 104 字节，名字保持很短。
    /// </summary>
    internal static string UnixSocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime))
        {
            return Path.Combine(runtime, "keep-password-activate.sock");
        }

        var user = new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
        var directory = Path.Combine(Path.GetTempPath(), "kp-" + (user.Length == 0 ? "user" : user));
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(directory, "activate.sock");
    }

    public void SignalActivate()
    {
        if (OperatingSystem.IsWindows())
        {
            _windowsEvent?.Set();
            return;
        }

        if (_socketPath is null)
        {
            return;
        }

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            client.Send(ActivatePayload);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
        }
    }

    public void StartListening(Action onActivate)
    {
        if (!IsPrimary || _disposed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            StartWindowsListener(onActivate);
            return;
        }

        StartUnixListener(onActivate);
    }

    private void StartWindowsListener(Action onActivate)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (!_disposed)
                {
                    if (_windowsEvent is not null && _windowsEvent.WaitOne(500) && !_disposed)
                    {
                        Dispatcher.UIThread.Post(onActivate);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "KeepPassword.Activate"
        };
        thread.Start();
    }

    private void StartUnixListener(Action onActivate)
    {
        if (_socketPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Socket listener;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            listener.Listen(4);
        }
        catch (Exception ex) when (ex is SocketException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        _listener = listener;
        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, onActivate, token), token);
    }

    private async Task AcceptLoopAsync(Socket listener, Action onActivate, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_disposed || token.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    using (client)
                    {
                        var buffer = new byte[ActivatePayload.Length];
                        var read = await client.ReceiveAsync(buffer, SocketFlags.None, token);
                        if (read > 0 && !_disposed)
                        {
                            Dispatcher.UIThread.Post(onActivate);
                        }
                    }
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listenCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _listenCts?.Dispose();
        _listenCts = null;

        try
        {
            _listener?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _listener = null;

        if (_socketPath is not null)
        {
            try
            {
                if (File.Exists(_socketPath))
                {
                    File.Delete(_socketPath);
                }
            }
            catch (IOException)
            {
            }
        }

        _windowsEvent?.Dispose();
        _mutex.Dispose();
    }
}
