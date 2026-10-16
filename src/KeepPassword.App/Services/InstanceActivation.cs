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
            _socketPath = Path.Combine(Path.GetTempPath(), "keep-password-activate.sock");
        }
    }

    public bool IsPrimary { get; }

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
        catch (IOException)
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
