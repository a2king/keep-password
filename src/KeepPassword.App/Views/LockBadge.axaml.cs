using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace KeepPassword.App.Views;

public partial class LockBadge : UserControl
{
    private readonly TranslateTransform _shackle = new();
    private readonly RotateTransform _key = new();
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _scale = new();
    private readonly DispatcherTimer _spinTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _spinClock = new();

    public LockBadge()
    {
        InitializeComponent();
        Shackle.RenderTransform = _shackle;
        Keyhole.RenderTransform = _key;
        SpinArc.RenderTransform = _spin;
        Root.RenderTransform = _scale;
        _spinTimer.Tick += (_, _) => _spin.Angle = _spinClock.Elapsed.TotalMilliseconds * 0.36 % 360;
    }

    public void Reset()
    {
        StopSpin();
        _shackle.Y = 0;
        _key.Angle = 0;
        _scale.ScaleX = _scale.ScaleY = 1;
    }

    public void StartSpin()
    {
        SpinArc.IsVisible = true;
        _spinClock.Restart();
        _spinTimer.Start();
    }

    public void StopSpin()
    {
        _spinTimer.Stop();
        SpinArc.IsVisible = false;
    }

    public async Task PlayTurnAsync()
    {
        StopSpin();
        await Tween.RunAsync(TimeSpan.FromMilliseconds(260), t => _key.Angle = 90 * t, Tween.CubicOut);
        await Tween.RunAsync(TimeSpan.FromMilliseconds(320), t => _shackle.Y = -8 * t, Tween.BackOut);
    }

    public Task PlayPopAsync() =>
        Tween.RunAsync(TimeSpan.FromMilliseconds(280), t => _scale.ScaleX = _scale.ScaleY = 1 + 0.12 * t, Tween.CubicOut);

    public Task PlayRejectAsync()
    {
        StopSpin();
        return Tween.RunAsync(TimeSpan.FromMilliseconds(380), t => _key.Angle = Math.Sin(t * Math.PI * 4) * 16 * (1 - t));
    }
}
