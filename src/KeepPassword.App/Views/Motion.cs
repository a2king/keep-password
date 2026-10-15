using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using KeepPassword.App.Services;

namespace KeepPassword.App.Views;

/// <summary>Short, cancellable presentation effects. Hiding and locking always remain immediate.</summary>
public static class Motion
{
    public static readonly AttachedProperty<bool> RevealOnShowProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("RevealOnShow", typeof(Motion));

    private static readonly ConditionalWeakTable<Control, State> States = new();

    static Motion()
    {
        RevealOnShowProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            var state = GetState(control);
            state.RevealOnShow = args.NewValue is true;
            if (state.RevealOnShow && control.IsEffectivelyVisible && control.GetVisualRoot() is not null)
            {
                _ = EnterAsync(control);
            }
        });
    }

    public static bool GetRevealOnShow(Control control) => control.GetValue(RevealOnShowProperty);

    public static void SetRevealOnShow(Control control, bool value) => control.SetValue(RevealOnShowProperty, value);

    public static bool IsEnabled => MotionPreferences.IsEnabled;

    public static Task EnterAsync(Control control, double offsetY = 8, double offsetX = 0) =>
        RunAsync(control, entering: true, 0, offsetX, offsetY, 200);

    public static Task RefreshAsync(Control control) => RunAsync(control, entering: true, 0.75, 0, 2, 140);

    public static Task FadeAsync(Control control) => RunAsync(control, entering: true, 0.88, 0, 0, 120);

    public static Task ExitAsync(Control control, double offsetY = -4, double offsetX = 0) =>
        RunAsync(control, entering: false, 0, offsetX, offsetY, 140);

    public static void Reset(Control control)
    {
        if (States.TryGetValue(control, out var state))
        {
            state.Reset();
        }
    }

    public static void ResetTree(Control root)
    {
        Reset(root);
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            Reset(control);
        }
    }

    public static void AttachWindow(Window window)
    {
        var state = GetState(window);
        if (state.WindowAttached)
        {
            return;
        }

        state.WindowAttached = true;
        window.Opened += (_, _) =>
        {
            if (window.Content is Control content)
            {
                _ = EnterAsync(content, 10);
            }
        };
        window.Closed += (_, _) => ResetTree(window);
    }

    private static State GetState(Control control) => States.GetValue(control, target =>
    {
        var state = new State();
        target.DetachedFromVisualTree += (_, _) => state.Reset();
        target.AttachedToVisualTree += (_, _) =>
        {
            if (state.RevealOnShow && target.IsEffectivelyVisible)
            {
                _ = EnterAsync(target);
            }
        };
        target.PropertyChanged += (_, args) =>
        {
            if (args.Property != Visual.IsVisibleProperty)
            {
                return;
            }

            if (!target.IsVisible)
            {
                state.Reset();
            }
            else if (state.RevealOnShow && target.IsEffectivelyVisible)
            {
                _ = EnterAsync(target);
            }
        };
        return state;
    });

    private static async Task RunAsync(Control control, bool entering, double startOpacity,
        double offsetX, double offsetY, int milliseconds)
    {
        var state = GetState(control);
        state.Reset();
        if (!IsEnabled || !control.IsEffectivelyVisible)
        {
            if (!entering)
            {
                control.IsVisible = false;
            }
            return;
        }

        var opacity = control.Opacity;
        var originalTransform = control.RenderTransform;
        var translation = new TranslateTransform();
        var group = new TransformGroup();
        if (originalTransform is not null)
        {
            group.Children.Add(originalTransform as Transform ?? new MatrixTransform(originalTransform.Value));
        }
        group.Children.Add(translation);
        control.SetCurrentValue(Visual.RenderTransformProperty, group);
        using var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        state.Restore = () =>
        {
            control.SetCurrentValue(Visual.OpacityProperty, opacity);
            control.SetCurrentValue(Visual.RenderTransformProperty, originalTransform);
        };
        try
        {
            await Tween.RunAsync(TimeSpan.FromMilliseconds(milliseconds), t =>
            {
                var remaining = entering ? 1 - t : t;
                control.SetCurrentValue(Visual.OpacityProperty, opacity * (entering ? startOpacity + (1 - startOpacity) * t : 1 - t));
                translation.X = offsetX * remaining;
                translation.Y = offsetY * remaining;
            }, Tween.CubicOut, cancellation.Token);
            if (!entering && !cancellation.IsCancellationRequested)
            {
                control.IsVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer action or an immediate hide owns the control now.
        }
        finally
        {
            if (ReferenceEquals(state.Cancellation, cancellation))
            {
                state.Cancellation = null;
                state.Restore?.Invoke();
                state.Restore = null;
            }
        }
    }

    private sealed class State
    {
        public bool RevealOnShow;
        public bool WindowAttached;
        public CancellationTokenSource? Cancellation;
        public Action? Restore;

        public void Reset()
        {
            var cancellation = Cancellation;
            Cancellation = null;
            cancellation?.Cancel();
            Restore?.Invoke();
            Restore = null;
        }
    }

}
