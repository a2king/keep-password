using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace KeepPassword.App.Controls.Details;

/// <summary>可替换的单条提示；显示时不占布局空间，回收时取消所有待执行延迟。</summary>
public class ToastHost : TemplatedControl
{
    public static readonly StyledProperty<string> MessageProperty = AvaloniaProperty.Register<ToastHost, string>(nameof(Message), "");
    private CancellationTokenSource? _pending;
    protected override Type StyleKeyOverride => typeof(ToastHost);
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public ToastHost() { IsHitTestVisible = false; DetachedFromVisualTree += (_, _) => Clear(); }
    public void Show(string message)
    {
        _pending?.Cancel();
        var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        SetCurrentValue(MessageProperty, message);
        PseudoClasses.Set(":shown", true);
        _ = HideAfterAsync(cancellation);
    }
    private async Task HideAfterAsync(CancellationTokenSource cancellation)
    {
        try { await Task.Delay(1800, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_pending, cancellation))
            { _pending = null; PseudoClasses.Set(":shown", false); }
            cancellation.Dispose();
        }
    }
    public void Clear()
    {
        var pending = _pending;
        _pending = null;
        pending?.Cancel();
        SetCurrentValue(MessageProperty, "");
        PseudoClasses.Set(":shown", false);
    }
}
