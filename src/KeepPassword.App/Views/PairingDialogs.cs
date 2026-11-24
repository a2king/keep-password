using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

/// <summary>设备配对的三个窗口：新设备等待批准、老设备批准、合并冲突逐条选择。</summary>
public static class PairingDialogs
{
    /// <summary>
    /// 新设备：发起配对并等待其他设备批准，返回收到的数据密钥（调用方清零），取消或失败返回 null。
    /// 确认码模式下必须由用户点「两边一致」才接收；扫码批准的密钥已认证，直接接收。
    /// </summary>
    public static async Task<byte[]?> RequestKeyAsync(Window owner, CloudSyncCoordinator sync, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var window = CreateWindow("从其他设备获取密钥", 600);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "正在发起请求…" };
        var code = new TextBlock { Text = "— — —", FontSize = 34, FontWeight = FontWeight.Bold, LetterSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        code.Classes.Add("mono");
        var codeLabel = Muted("确认码");
        var approver = Muted("");
        var qr = new QrView { Width = 150, Height = 150 };
        var qrPanel = new StackPanel { Spacing = 6, Width = 150, IsVisible = false, Children = { qr, Muted("手机端可扫码批准（即将推出）", 11) } };
        var confirm = new Button { Content = "两边一致，继续", IsEnabled = false };
        confirm.Classes.Add("accent");
        var retry = new Button { Content = "重新发起", IsVisible = false };
        retry.Classes.Add("soft");
        var cancel = new Button { Content = "取消" };
        cancel.Classes.Add("soft");

        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "在另一台已开启云同步的电脑上打开 Keep Password 并保持解锁，它会弹出「新设备请求加入」。"
                   + "核对两边的确认码完全一致后，先在那台电脑上点「批准」，再在这里点「两边一致，继续」。不需要密钥文件。"
        });
        left.Children.Add(codeLabel);
        left.Children.Add(code);
        left.Children.Add(approver);
        left.Children.Add(status);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,Auto") };
        columns.Children.Add(left);
        Grid.SetColumn(qrPanel, 2);
        columns.Children.Add(qrPanel);

        byte[]? result = null;
        PairingRequest? request = null;
        var userConfirmed = false;
        PairingProgress? pending = null;
        using var cts = new CancellationTokenSource();
        window.Closed += (_, _) => cts.Cancel();

        void Finish(byte[] key)
        {
            result = key;
            window.Close();
        }

        confirm.Click += (_, _) =>
        {
            userConfirmed = true;
            confirm.IsEnabled = false;
            if (pending is { Stage: PairingStage.Approved, DataKey: { } key }) Finish(key);
            else status.Text = "已确认。等待另一台电脑批准…";
        };
        cancel.Click += (_, _) => window.Close();

        async Task RunAsync()
        {
            try
            {
                request?.Dispose();
                request = null;
                pending = null;
                userConfirmed = false;
                code.Text = "— — —";
                approver.Text = "";
                retry.IsVisible = false;
                confirm.IsEnabled = false;
                request = await sync.StartPairingAsync(cts.Token);
                qr.Matrix = PairingCrypto.QrMatrix(request.QrPayload);
                qrPanel.IsVisible = true;
                status.Text = "等待其他设备响应…";
                while (!cts.IsCancellationRequested)
                {
                    var progress = await request.PollAsync(cts.Token);
                    if (progress.Code is { } shown)
                    {
                        code.Text = shown;
                        approver.Text = string.IsNullOrEmpty(progress.ApproverDeviceName) ? "" : $"响应设备：{progress.ApproverDeviceName}";
                    }

                    switch (progress.Stage)
                    {
                        case PairingStage.Confirming:
                            if (!userConfirmed)
                            {
                                confirm.IsEnabled = true;
                                status.Text = "请核对另一台电脑上显示的确认码。";
                            }
                            break;
                        case PairingStage.Approved:
                            await request.CompleteAsync(CancellationToken.None);
                            if (progress.KeyAuthenticated || userConfirmed)
                            {
                                Finish(progress.DataKey!);
                                return;
                            }

                            pending = progress;
                            confirm.IsEnabled = true;
                            status.Text = "另一台电脑已批准。确认两边确认码一致后，点「两边一致，继续」。";
                            return;
                        case PairingStage.Rejected:
                            End("另一台设备拒绝了这次请求。");
                            return;
                        case PairingStage.Cancelled:
                        case PairingStage.Expired:
                            End("请求已过期。");
                            return;
                    }

                    await Task.Delay(interval, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SyncApiException ex)
            {
                End(ex.Message);
            }
        }

        void End(string message)
        {
            status.Text = message;
            confirm.IsEnabled = false;
            retry.IsVisible = true;
            qrPanel.IsVisible = false;
        }

        retry.Click += (_, _) => _ = RunAsync();
        window.Content = Content("从其他设备获取密钥", columns, Buttons(retry, cancel, confirm));
        window.Opened += (_, _) => _ = RunAsync();
        await window.ShowDialog(owner);
        if (result is null && request is not null && pending is null)
        {
            await request.CancelAsync(CancellationToken.None);
        }

        if (result is null && pending?.DataKey is { } unused)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(unused);
        }

        request?.Dispose();
        return result;
    }

    /// <summary>老设备：显示新设备名称和确认码，返回用户是否批准。</summary>
    public static async Task<bool> ConfirmApprovalAsync(Window owner, PairingApproval approval)
    {
        var window = CreateWindow("新设备请求加入", 460);
        var code = new TextBlock { Text = approval.Code, FontSize = 34, FontWeight = FontWeight.Bold, LetterSpacing = 2 };
        code.Classes.Add("mono");
        var approve = new Button { Content = "批准" };
        approve.Classes.Add("accent");
        var reject = new Button { Content = "拒绝" };
        reject.Classes.Add("soft");
        var approved = false;
        approve.Click += (_, _) => { approved = true; window.Close(); };
        reject.Click += (_, _) => window.Close();
        var platform = string.IsNullOrWhiteSpace(approval.Platform) ? "" : $"（{approval.Platform}）";
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = $"「{approval.DeviceName}」{platform}请求加入云同步，获取解开保险库的密钥。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Muted("确认码"));
        body.Children.Add(code);
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "只有在新设备上显示的确认码与这里完全一致、并且是你本人操作时才批准。不认识的请求请拒绝，并修改网站密码。"
        });
        window.Content = Content("新设备请求加入", body, Buttons(reject, approve));
        await window.ShowDialog(owner);
        return approved;
    }

    /// <summary>合并冲突逐条选择用本机还是云端。返回选择；取消返回 null（同步保持暂停，本地不改）。</summary>
    public static async Task<IReadOnlyDictionary<Guid, MergeSide>?> ChooseConflictsAsync(Window owner, IReadOnlyList<MergeConflict> conflicts)
    {
        var window = CreateWindow("选择冲突条目保留哪一边", 620);
        var choices = conflicts.ToDictionary(conflict => conflict.Id, conflict => conflict.Suggested);
        var list = new StackPanel { Spacing = 8 };
        foreach (var conflict in conflicts)
        {
            var group = "conflict-" + conflict.Id.ToString("N");
            var local = new RadioButton { GroupName = group, Content = $"本机（{conflict.LocalUpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}）", IsChecked = conflict.Suggested == MergeSide.Local };
            var remote = new RadioButton { GroupName = group, Content = $"云端（{conflict.RemoteUpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}）", IsChecked = conflict.Suggested == MergeSide.Remote };
            local.IsCheckedChanged += (_, _) => { if (local.IsChecked == true) choices[conflict.Id] = MergeSide.Local; };
            remote.IsCheckedChanged += (_, _) => { if (remote.IsChecked == true) choices[conflict.Id] = MergeSide.Remote; };
            var name = conflict.LocalName == conflict.RemoteName ? conflict.LocalName : $"{conflict.LocalName} / {conflict.RemoteName}";
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = $"{name}（{VaultItemKinds.DisplayName(conflict.Kind)}）", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(Muted("不同之处：" + string.Join("、", conflict.Differences)));
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { local, remote } });
            var card = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = row };
            card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Kp.Border"));
            list.Children.Add(card);
        }

        var ok = new Button { Content = "按所选合并" };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "取消" };
        cancel.Classes.Add("soft");
        var accepted = false;
        ok.Click += (_, _) => { accepted = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"有 {conflicts.Count} 个条目本机和云端都改过。默认选中较新的一边，可以逐条改。两边都没有冲突的条目会自动合并。取消则同步保持暂停，本地保险库不改动。"
        });
        body.Children.Add(new ScrollViewer { MaxHeight = 360, Content = list });
        window.Content = Content("选择冲突条目保留哪一边", body, Buttons(cancel, ok));
        await window.ShowDialog(owner);
        return accepted ? choices : null;
    }

    private static TextBlock Muted(string text, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("muted");
        return block;
    }

    private static Window CreateWindow(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static StackPanel Content(string title, Control body, Control buttons)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(body);
        panel.Children.Add(buttons);
        return panel;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }
}

/// <summary>按点阵画二维码：白底黑块，不依赖图片解码。</summary>
public sealed class QrView : Control
{
    private bool[,]? _matrix;

    public bool[,]? Matrix
    {
        get => _matrix;
        set { _matrix = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds, 6);
        if (_matrix is not { } matrix) return;
        var size = matrix.GetLength(0);
        var cell = Math.Floor(Math.Min(bounds.Width, bounds.Height) / size);
        if (cell <= 0) return;
        var offset = (Math.Min(bounds.Width, bounds.Height) - cell * size) / 2;
        for (var x = 0; x < size; x++)
        for (var y = 0; y < size; y++)
            if (matrix[x, y])
                context.FillRectangle(Brushes.Black, new Rect(offset + x * cell, offset + y * cell, cell, cell));
    }
}
