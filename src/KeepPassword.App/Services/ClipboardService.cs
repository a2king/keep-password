using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Services;

/// <summary>所有复制共用到期状态；写入成功即可反馈，敏感内容在后台按原规则清除。</summary>
public static class ClipboardService
{
    private static readonly ClipboardCopyCoordinator Coordinator = new(Task.Delay, ExpiringClipboard.DefaultLifetime);

    public static Task<bool> CopyAsync(TopLevel? topLevel, string value, bool sensitive) => topLevel?.Clipboard is { } clipboard
        ? CopyAsync(clipboard, value, sensitive)
        : Task.FromResult(false);

    public static Task<bool> CopyAsync(IClipboard clipboard, string value, bool sensitive) =>
        Coordinator.CopyAsync(new AvaloniaClipboard(clipboard), value, sensitive);

    private sealed class AvaloniaClipboard(IClipboard clipboard) : ITextClipboard
    {
        public Task<string?> ReadAsync() => Dispatcher.UIThread.InvokeAsync(() => clipboard.TryGetTextAsync());

        public Task WriteAsync(string? value) => Dispatcher.UIThread.InvokeAsync(() => clipboard.SetTextAsync(value));
    }
}

/// <summary>沿用 ExpiringClipboard 的“到期且内容相同才清空”规则，并防止旧任务与新复制竞态。</summary>
internal sealed class ClipboardCopyCoordinator(Func<TimeSpan, CancellationToken, Task> delay, TimeSpan lifetime)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingCopy? _pending;

    internal Task ExpirationTask { get; private set; } = Task.CompletedTask;

    public async Task<bool> CopyAsync(ITextClipboard clipboard, string value, bool sensitive)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _pending?.Cancellation.Cancel();
            _pending = null;
            try
            {
                await clipboard.WriteAsync(value).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }

            if (sensitive && value.Length > 0)
            {
                var pending = new PendingCopy(clipboard, value);
                _pending = pending;
                ExpirationTask = ExpireAsync(pending);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExpireAsync(PendingCopy pending)
    {
        try
        {
            await delay(lifetime, pending.Cancellation.Token).ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_pending, pending) || pending.Cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (await pending.Clipboard.ReadAsync().ConfigureAwait(false) == pending.Value)
                {
                    await pending.Clipboard.WriteAsync("").ConfigureAwait(false);
                }

                _pending = null;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // 失去剪贴板访问或被后一次复制取消时，不影响已经完成的复制反馈。
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }

                pending.Value = "";
                pending.Cancellation.Dispose();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private sealed class PendingCopy(ITextClipboard clipboard, string value)
    {
        public ITextClipboard Clipboard { get; } = clipboard;

        public string Value { get; set; } = value;

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
