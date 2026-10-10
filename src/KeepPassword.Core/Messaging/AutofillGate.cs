namespace KeepPassword.Core.Messaging;

public sealed class AutofillGate
{
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly TimeSpan _window;
    private readonly int _maxAttempts;
    private int _held;

    public AutofillGate(TimeSpan? window = null, int maxAttempts = 8)
    {
        _window = window ?? TimeSpan.FromSeconds(30);
        _maxAttempts = maxAttempts;
    }

    public bool TryEnter(string key, out IDisposable? lease)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            while (_attempts.Count > 0 && now - _attempts.Peek() > _window)
            {
                _attempts.Dequeue();
            }

            if (_attempts.Count >= _maxAttempts || _held > 0)
            {
                lease = null;
                return false;
            }

            _attempts.Enqueue(now);
            _held = 1;
            _ = key;
            lease = new Release(this);
            return true;
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _held = 0;
        }
    }

    private sealed class Release : IDisposable
    {
        private AutofillGate? _gate;

        public Release(AutofillGate gate) => _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Exit();
        }
    }
}
