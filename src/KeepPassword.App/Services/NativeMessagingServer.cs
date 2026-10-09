using System.Net;
using System.Net.Sockets;
using KeepPassword.Core.Messaging;

namespace KeepPassword.App.Services;

public sealed class NativeMessagingServer : IDisposable
{
    private readonly Func<string, CancellationToken, Task<string>> _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public NativeMessagingServer(Func<string, CancellationToken, Task<string>> handler)
    {
        _handler = handler;
    }

    public string? StartError { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, NativeMessagingDefaults.Port);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            StartError = "本机消息端口被占用：" + ex.Message;
            return;
        }

        _ = AcceptLoop(_cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 120000;
            client.SendTimeout = 15000;
            var stream = client.GetStream();
            try
            {
                var message = NativeMessageFraming.Read(stream);
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                var response = await _handler(message, cancellationToken);
                NativeMessageFraming.Write(stream, response);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                try
                {
                    NativeMessageFraming.Write(stream, AutofillProtocol.Serialize(AutofillResponse.Error("客户端处理消息失败。")));
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
