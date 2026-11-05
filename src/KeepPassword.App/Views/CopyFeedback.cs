using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace KeepPassword.App.Views;

/// <summary>统一的复制成功反馈：短暂显示勾选图标并播放轻微刷新动效。</summary>
internal static class CopyFeedback
{
    private sealed class State(PathIcon? icon)
    {
        // 连续复制共用这一份初始图标，不能把上一轮已显示的勾选当作原图标。
        public PathIcon? Icon = icon;
        public Geometry? Original = icon?.Data;
        public CancellationTokenSource? Cancellation;
    }

    private static readonly ConditionalWeakTable<Button, State> States = new();

    public static async Task ShowAsync(Button button, TimeSpan? duration = null)
    {
        if (!States.TryGetValue(button, out var state))
        {
            var icon = button.Content as PathIcon
                ?? button.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault();
            state = new State(icon);
            States.Add(button, state);
            button.DetachedFromVisualTree += OnButtonDetached;
        }

        using var cancellation = new CancellationTokenSource();
        var previous = state.Cancellation;
        // 先转移所有权，再取消旧延迟，避免旧调用的 finally 提前还原本次反馈。
        state.Cancellation = cancellation;
        previous?.Cancel();

        if (Application.Current?.TryFindResource("Icon.Check", out var resource) == true
            && resource is Geometry check)
        {
            if (state.Icon is not null)
            {
                state.Icon.Data = check;
            }
        }

        button.Classes.Add("copy-success");
        _ = Motion.RefreshAsync(button);

        try
        {
            await Task.Delay(duration ?? TimeSpan.FromMilliseconds(1400), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 快速再次复制或控件回收时，交由当前反馈调用/回收处理负责还原。
        }
        finally
        {
            if (ReferenceEquals(state.Cancellation, cancellation))
            {
                Restore(button, state);
            }
        }
    }

    private static void OnButtonDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button && States.TryGetValue(button, out var state))
        {
            var cancellation = state.Cancellation;
            Restore(button, state);
            cancellation?.Cancel();
        }
    }

    private static void Restore(Button button, State state)
    {
        if (state.Icon is not null)
        {
            state.Icon.Data = state.Original;
        }
        button.Classes.Remove("copy-success");
        button.DetachedFromVisualTree -= OnButtonDetached;
        state.Cancellation = null;
        state.Icon = null;
        state.Original = null;
        States.Remove(button);
    }
}
