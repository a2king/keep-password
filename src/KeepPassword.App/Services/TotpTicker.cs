using Avalonia.Threading;

namespace KeepPassword.App.Services;

/// <summary>可见验证码共享一个 UI 时钟；最后一个订阅释放后立即停止。</summary>
public static class TotpTicker
{
    private static readonly HashSet<Subscription> Subscriptions = [];
    private static DispatcherTimer? _timer;

    internal static int SubscriberCount => Subscriptions.Count;

    internal static bool IsRunning => _timer?.IsEnabled == true;

    public static IDisposable Subscribe(Action<DateTimeOffset> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        Dispatcher.UIThread.VerifyAccess();
        var subscription = new Subscription(tick);
        Subscriptions.Add(subscription);
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Publish(DateTimeOffset.UtcNow);
        }

        _timer.Start();
        tick(DateTimeOffset.UtcNow);
        return subscription;
    }

    internal static void Publish(DateTimeOffset now)
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (var subscription in Subscriptions.ToArray())
        {
            subscription.Tick?.Invoke(now);
        }
    }

    private static void Remove(Subscription subscription)
    {
        Subscriptions.Remove(subscription);
        if (Subscriptions.Count == 0)
        {
            _timer?.Stop();
        }
    }

    private sealed class Subscription(Action<DateTimeOffset> tick) : IDisposable
    {
        private Action<DateTimeOffset>? _tick = tick;

        internal Action<DateTimeOffset>? Tick => _tick;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _tick, null) is null)
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Remove(this);
            }
            else
            {
                Dispatcher.UIThread.Post(() => Remove(this));
            }
        }
    }
}
