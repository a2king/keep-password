using Avalonia.Input.Platform;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Services;

public static class SecretClipboard
{
    private static readonly ExpiringClipboard Clipboard = new();
    private static CancellationTokenSource? _pending;

    public static async Task CopyAsync(IClipboard clipboard, string value)
    {
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        await Clipboard.CopyAsync(new AvaloniaClipboard(clipboard), value, ExpiringClipboard.DefaultLifetime, Task.Delay, _pending.Token);
    }

    private sealed class AvaloniaClipboard(IClipboard clipboard) : ITextClipboard
    {
        public Task<string?> ReadAsync() => clipboard.TryGetTextAsync();

        public Task WriteAsync(string? value) => clipboard.SetTextAsync(value);
    }
}
