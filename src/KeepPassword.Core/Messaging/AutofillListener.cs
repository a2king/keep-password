using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KeepPassword.Core.Messaging;

public sealed class AutofillListener : IDisposable
{
    private readonly Func<string, CancellationToken, Task<string>> _handler;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private string? _publishedPath;

    private AutofillListener(Func<string, CancellationToken, Task<string>> handler, TcpListener listener, AutofillChannel channel)
    {
        _handler = handler;
        _listener = listener;
        Channel = channel;
    }

    public AutofillChannel Channel { get; }

    public static AutofillListener Start(Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var channel = AutofillChannel.Create(port);
        var server = new AutofillListener(handler, listener, channel);
        _ = server.AcceptLoop();
        return server;
    }

    public void Publish(string path)
    {
        Channel.Publish(path);
        _publishedPath = path;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        if (_publishedPath is not null)
        {
            try
            {
                File.Delete(_publishedPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
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

                if (!AutofillProtocol.TryParseDiscover(message, out var request) || !Channel.TokenEquals(request.ClientToken))
                {
                    NativeMessageFraming.Write(stream, AutofillProtocol.Serialize(AutofillResponse.Error("本机填充通道未授权。")));
                    return;
                }

                var response = await _handler(message, _cts.Token);
                NativeMessageFraming.Write(stream, response);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException)
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
