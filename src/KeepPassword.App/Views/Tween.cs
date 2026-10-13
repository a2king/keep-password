using System.Diagnostics;
using Avalonia.Threading;

namespace KeepPassword.App.Views;

internal static class Tween
{
    public static Task RunAsync(TimeSpan duration, Action<double> step, Func<double, double>? ease = null)
    {
        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            step(ease is null ? t : ease(t));
            if (t >= 1)
            {
                timer.Stop();
                done.TrySetResult();
            }
        };
        step(0);
        timer.Start();
        return done.Task;
    }

    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);

    public static double BackOut(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
