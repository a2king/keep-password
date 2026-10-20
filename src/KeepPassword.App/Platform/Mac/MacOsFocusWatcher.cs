using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// 仅 macOS：轮询前台焦点，密码框获得焦点时通知（对齐浏览器扩展「聚焦即提示」）。
/// </summary>
internal sealed class MacOsFocusWatcher : IDisposable
{
    // 不标 SupportedOSPlatform：由 AppHost 在 IsMacOS 分支创建，避免跨平台调用站点告警。

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly IPasswordFieldDetector _detector;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private string? _lastSeenKey;
    private string? _lastOfferedKey;
    private bool _disposed;

    public MacOsFocusWatcher(IPasswordFieldDetector detector)
    {
        _detector = detector;
        _loop = Task.Run(PollLoopAsync);
    }

    /// <summary>发现新的密码框焦点时触发（同一字段在失焦前只触发一次）。</summary>
    public event Action<DetectedPasswordField>? PasswordFieldFocused;

    /// <summary>为 true 时暂停轮询回调（例如正在弹窗或本程序内操作）。</summary>
    public bool Paused { get; set; }

    private async Task PollLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (Paused || _disposed)
            {
                continue;
            }

            DetectedPasswordField? field = null;
            try
            {
                field = _detector.DetectForeground().FirstOrDefault();
            }
            catch (Exception)
            {
                field = null;
            }

            var key = field?.FieldKey;
            if (key is null)
            {
                _lastSeenKey = null;
                continue;
            }

            if (key == _lastSeenKey)
            {
                continue;
            }

            _lastSeenKey = key;
            if (key == _lastOfferedKey)
            {
                continue;
            }

            _lastOfferedKey = key;
            try
            {
                PasswordFieldFocused?.Invoke(field!);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>用户取消或填充结束后，允许同一字段在再次聚焦时重新提示（需先失焦）。</summary>
    public void ResetOffer(string? fieldKey = null)
    {
        if (fieldKey is null || fieldKey == _lastOfferedKey)
        {
            _lastOfferedKey = null;
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
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();
    }
}
