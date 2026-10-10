namespace KeepPassword.Core.Security;

public interface ITextClipboard
{
    Task<string?> ReadAsync();

    Task WriteAsync(string? value);
}

public sealed class ExpiringClipboard
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);

    public async Task CopyAsync(
        ITextClipboard clipboard,
        string value,
        TimeSpan lifetime,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(delay);
        await clipboard.WriteAsync(value).ConfigureAwait(false);
        try
        {
            await delay(lifetime, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var current = await clipboard.ReadAsync().ConfigureAwait(false);
        if (current == value)
        {
            await clipboard.WriteAsync("").ConfigureAwait(false);
        }
    }
}
