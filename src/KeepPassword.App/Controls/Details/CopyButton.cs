using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.App.Services;

namespace KeepPassword.App.Controls.Details;

public sealed class DetailCopiedEventArgs(RoutedEvent routedEvent, string message) : RoutedEventArgs(routedEvent)
{
    public string Message { get; } = message;
}

/// <summary>复制写入完成即反馈；剪贴板到期清理由统一服务后台处理。</summary>
public class CopyButton : Button
{
    public static readonly StyledProperty<string> CopyValueProperty = AvaloniaProperty.Register<CopyButton, string>(nameof(CopyValue), "");
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<CopyButton, string>(nameof(Label), "内容");
    public static readonly StyledProperty<bool> IsSensitiveProperty = AvaloniaProperty.Register<CopyButton, bool>(nameof(IsSensitive));
    public static readonly RoutedEvent<DetailCopiedEventArgs> CopiedEvent = RoutedEvent.Register<CopyButton, DetailCopiedEventArgs>("Copied", RoutingStrategies.Bubble);
    private CancellationTokenSource? _feedback;
    protected override Type StyleKeyOverride => typeof(CopyButton);
    public string CopyValue { get => GetValue(CopyValueProperty); set => SetValue(CopyValueProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsSensitive { get => GetValue(IsSensitiveProperty); set => SetValue(IsSensitiveProperty, value); }
    public event Action<string>? Copied;

    public CopyButton()
    {
        DetachedFromVisualTree += (_, _) => Reset();
        UpdateAccessibleName();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) UpdateAccessibleName();
    }
    private void UpdateAccessibleName()
    {
        AutomationProperties.SetName(this, "复制" + Label);
        ToolTip.SetTip(this, "复制" + Label);
    }
    protected override async void OnClick()
    {
        base.OnClick();
        await CopyAsync();
    }
    public async Task<bool> CopyAsync()
    {
        if (string.IsNullOrEmpty(CopyValue) || TopLevel.GetTopLevel(this) is not { } top) return false;
        if (!await ClipboardService.CopyAsync(top, CopyValue, IsSensitive)) return false;
        if (TopLevel.GetTopLevel(this) != top || !IsEffectivelyVisible) return true;
        _feedback?.Cancel();
        var cancellation = new CancellationTokenSource();
        _feedback = cancellation;
        PseudoClasses.Set(":copied", true);
        var message = "已复制" + Label + (IsSensitive ? " · 30 秒后自动清除" : "");
        Copied?.Invoke(message);
        RaiseEvent(new DetailCopiedEventArgs(CopiedEvent, message));
        _ = ResetAfterAsync(cancellation);
        // 延迟任务拥有自己的 CTS；复制方法立即返回，不阻塞宿主反馈。
        return true;
    }
    private async Task ResetAfterAsync(CancellationTokenSource cancellation)
    {
        try { await Task.Delay(1500, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_feedback, cancellation))
            { _feedback = null; PseudoClasses.Set(":copied", false); }
            cancellation.Dispose();
        }
    }
    public void Reset()
    {
        var pending = _feedback;
        _feedback = null;
        pending?.Cancel();
        PseudoClasses.Set(":copied", false);
    }
}
