using Avalonia.Threading;
using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Services;

public sealed class AutofillWatcher
{
    private readonly DispatcherTimer _timer;
    private readonly IPasswordFieldDetector _detector;
    private readonly Func<DetectedPasswordField, Task> _onField;
    private string? _lastKey;
    private int _busy;

    public AutofillWatcher(IPasswordFieldDetector detector, Func<DetectedPasswordField, Task> onField)
    {
        _detector = detector;
        _onField = onField;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _ = TickAsync();
    }

    public void Start()
    {
        _lastKey = null;
        if (_detector.IsSupported)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _lastKey = null;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var field = _detector.DetectForeground().FirstOrDefault();
            if (field is null)
            {
                _lastKey = null;
                return;
            }

            if (field.FieldKey == _lastKey)
            {
                return;
            }

            _lastKey = field.FieldKey;
            await _onField(field);
        }
        catch (Exception)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
