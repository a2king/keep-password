using KeepPassword.Core.Messaging;

namespace KeepPassword.App.Services;

public sealed class NativeMessagingServer : IDisposable
{
    private readonly AutofillListener _listener;

    public NativeMessagingServer(Func<string, CancellationToken, Task<string>> handler)
    {
        _listener = AutofillListener.Start(handler);
    }

    public string? StartError { get; private set; }

    public void Start()
    {
        try
        {
            _listener.Publish(AutofillChannel.SessionFile());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StartError = "无法发布本机填充通道：" + ex.Message;
        }
    }

    public void Dispose() => _listener.Dispose();
}