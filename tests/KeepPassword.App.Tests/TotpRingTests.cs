using KeepPassword.App.ViewModels;

namespace KeepPassword.App.Tests;

public class TotpRingTests
{
    private static TotpRowView Row() => new() { AccountId = Guid.NewGuid(), EntryId = Guid.NewGuid(), Name = "测试", Secret = "JBSWY3DPEHPK3PXP" };

    [Fact]
    public void Ring_SweepFollowsRemainingOverPeriodAndWarnsInLastFiveSeconds()
    {
        var row = Row();
        row.Remaining = 30;
        Assert.Equal(360, row.RemainingSweep, precision: 3);
        Assert.False(row.IsUrgent);

        row.Remaining = 15;
        Assert.Equal(180, row.RemainingSweep, precision: 3);

        // 最后 5 秒变警示色。
        row.Remaining = 5;
        Assert.True(row.IsUrgent);
        row.Remaining = 6;
        Assert.False(row.IsUrgent);

        // 60 秒周期的密钥：同样剩 15 秒，圆弧只剩四分之一。
        row.Period = 60;
        row.Remaining = 15;
        Assert.Equal(90, row.RemainingSweep, precision: 3);

        // 无法识别的密钥：不显示圆环。
        row.Remaining = 0;
        Assert.False(row.HasCountdown);
        Assert.Equal(0, row.RemainingSweep);
    }

    [Fact]
    public void Ring_RaisesChangesSoTheViewRedrawsEverySecond()
    {
        var row = Row();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Remaining = 12;
        Assert.Contains(nameof(TotpRowView.RemainingSweep), changed);
        Assert.Contains(nameof(TotpRowView.IsUrgent), changed);
        Assert.Contains(nameof(TotpRowView.HasCountdown), changed);
    }
}
