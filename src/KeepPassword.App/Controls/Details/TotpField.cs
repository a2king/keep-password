using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KeepPassword.App.Services;
using KeepPassword.Core.Totp;

namespace KeepPassword.App.Controls.Details;

/// <summary>只订阅共享时钟；隐藏、离页或回收时释放订阅，不为单个验证码创建计时器。</summary>
public class TotpField : SecretField
{
    // Secret 是 Value 的同一属性别名，编辑器始终绑定真实密钥，绝不把验证码写回草稿。
    public static readonly StyledProperty<string> SecretProperty = ValueProperty.AddOwner<TotpField>();
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<TotpField, bool>(nameof(IsActive), true);
    public static readonly DirectProperty<TotpField, string> CodeProperty = AvaloniaProperty.RegisterDirect<TotpField, string>(nameof(Code), control => control.Code);
    public static readonly DirectProperty<TotpField, int> RemainingSecondsProperty = AvaloniaProperty.RegisterDirect<TotpField, int>(nameof(RemainingSeconds), control => control.RemainingSeconds);
    public static readonly DirectProperty<TotpField, double> RemainingSweepProperty = AvaloniaProperty.RegisterDirect<TotpField, double>(nameof(RemainingSweep), control => control.RemainingSweep);
    private IDisposable? _subscription;
    private string _code = "— — —";
    private int _remaining;
    private double _sweep;
    private readonly TotpValuePresenter _presentation;
    private readonly List<Control> _ancestors = [];
    protected override Type StyleKeyOverride => typeof(TotpField);
    // 验证码展示是自定义内容，但编辑按钮仍需进入真实密钥的文本编辑器。
    protected override bool SupportsInlineEditing => true;
    public string Secret { get => GetValue(SecretProperty); set => SetValue(SecretProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public string Code => _code;
    public int RemainingSeconds => _remaining;
    public double RemainingSweep => _sweep;
    public TotpField()
    {
        Label = "验证码";
        IsMono = true;
        _presentation = new TotpValuePresenter { DataContext = this };
        ValueContent = _presentation;
        AttachedToVisualTree += (_, _) =>
        {
            foreach (var ancestor in this.GetVisualAncestors().OfType<Control>())
            {
                _ancestors.Add(ancestor);
                ancestor.PropertyChanged += OnAncestorChanged;
            }
            RefreshSubscription();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Stop();
            foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
            _ancestors.Clear();
        };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SecretProperty) Tick(DateTimeOffset.UtcNow);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty || change.Property == SecretProperty)
            RefreshSubscription();
    }
    private void RefreshSubscription()
    {
        if (IsActive && IsEffectivelyVisible && this.GetVisualRoot() is not null && !string.IsNullOrWhiteSpace(Secret))
        {
            _subscription ??= TotpTicker.Subscribe(Tick);
        }
        else Stop();
    }
    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty) RefreshSubscription();
    }
    private void Stop() { _subscription?.Dispose(); _subscription = null; }
    private void Tick(DateTimeOffset now)
    {
        if (!TotpGenerator.TryGenerate(Secret, now.ToUnixTimeSeconds(), out var code, out var remaining))
        {
            SetAndRaise(CodeProperty, ref _code, "— — —");
            SetAndRaise(RemainingSecondsProperty, ref _remaining, 0);
            SetAndRaise(RemainingSweepProperty, ref _sweep, 0);
            SetCurrentValue(CopyValueProperty, "");
            _presentation?.Classes.Remove("urgent");
            return;
        }
        var split = code.Length / 2;
        var formatted = code[..split] + " " + code[split..];
        var codeChanged = formatted != _code;
        SetAndRaise(CodeProperty, ref _code, formatted);
        // 每秒只更新圆环；仅真正换码时触发一次有限的透明度过渡。
        if (codeChanged) _presentation?.PulseCode();
        SetAndRaise(RemainingSecondsProperty, ref _remaining, remaining);
        TotpGenerator.TryParseSecret(Secret, out var settings);
        SetAndRaise(RemainingSweepProperty, ref _sweep, remaining / (double)settings.PeriodSeconds * 360);
        SetCurrentValue(CopyValueProperty, code);
        _presentation?.Classes.Set("urgent", remaining <= 5);
    }
    protected override string GetCopyValue() => CopyValue ?? "";
    public override void EndEdit(bool cancel = false)
    {
        if (!cancel && !string.IsNullOrWhiteSpace(Secret) && !TotpGenerator.TryParseSecret(Secret, out _))
        {
            SetCurrentValue(ValidationMessageProperty, "验证码密钥格式不正确");
            return;
        }
        SetCurrentValue(ValidationMessageProperty, "");
        base.EndEdit(cancel);
    }
    protected override void OnValueActivated() => _ = CopyCurrentAsync();
}

/// <summary>验证码的纯视觉部分，字号、圆环和临界颜色都由主题定义。</summary>
public class TotpValuePresenter : TemplatedControl
{
    private TextBlock? _codeText;
    private int _pulseVersion;
    protected override Type StyleKeyOverride => typeof(TotpValuePresenter);
    public TotpValuePresenter()
    {
        AttachedToVisualTree += (_, _) => MotionPreferences.Changed += OnMotionChanged;
        DetachedFromVisualTree += (_, _) =>
        { MotionPreferences.Changed -= OnMotionChanged; CancelPulse(); };
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        CancelPulse();
        base.OnApplyTemplate(e);
        _codeText = e.NameScope.Find<TextBlock>("PART_Code");
    }
    private bool CanAnimate() => IsEffectivelyVisible && this.GetVisualRoot() is not null && MotionPreferences.IsEnabled
        && !Classes.Contains("no-motion")
        && !this.GetVisualAncestors().OfType<Control>().Any(control => control.Classes.Contains("no-motion"));
    public void PulseCode()
    {
        if (_codeText is not { } text || !CanAnimate()) { CancelPulse(); return; }
        var version = ++_pulseVersion;
        // 先无过渡降到0.45，再下一帧恢复1；恢复过程使用主题的120ms DoubleTransition。
        var transitions = text.Transitions;
        text.SetCurrentValue(Animatable.TransitionsProperty, null);
        text.SetCurrentValue(OpacityProperty, 0.45);
        text.SetCurrentValue(Animatable.TransitionsProperty, transitions);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _pulseVersion || !ReferenceEquals(_codeText, text)) return;
            if (!CanAnimate()) { CancelPulse(); return; }
            text.SetCurrentValue(OpacityProperty, 1);
        }, DispatcherPriority.Background);
    }
    private void OnMotionChanged() { if (!MotionPreferences.IsEnabled) CancelPulse(); }
    private void CancelPulse()
    {
        ++_pulseVersion;
        if (_codeText is not { } text) return;
        var transitions = text.Transitions;
        text.SetCurrentValue(Animatable.TransitionsProperty, null);
        text.SetCurrentValue(OpacityProperty, 1);
        text.SetCurrentValue(Animatable.TransitionsProperty, transitions);
    }
}
