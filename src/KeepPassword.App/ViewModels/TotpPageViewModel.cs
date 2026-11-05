using KeepPassword.Core.Security;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class TotpRowView : ViewModelBase
{
    private string _code = "";
    private int _remaining;
    private int _period = 30;

    public required Guid AccountId { get; init; }

    public required Guid EntryId { get; init; }

    public required string Name { get; init; }

    public required string? Secret { get; init; }

    public string Code
    {
        get => _code;
        set => Set(ref _code, value);
    }

    public int Remaining
    {
        get => _remaining;
        set
        {
            if (Set(ref _remaining, value))
            {
                OnPropertyChanged(nameof(RemainingSweep));
                OnPropertyChanged(nameof(IsUrgent));
                OnPropertyChanged(nameof(HasCountdown));
            }
        }
    }

    /// <summary>验证码周期（秒），来自密钥里的 period 参数，默认 30。</summary>
    public int Period
    {
        get => _period;
        set
        {
            if (Set(ref _period, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(RemainingSweep));
            }
        }
    }

    // 倒计时圆环与条目详情一致：圆弧角度 = 剩余 / 周期 × 360°，中心显示剩余秒数，最后 5 秒变警示色。
    public double RemainingSweep => Remaining <= 0 ? 0 : Remaining / (double)Period * 360;

    public bool IsUrgent => Remaining is > 0 and <= 5;

    public bool HasCountdown => Remaining > 0;
}

public sealed class TotpPageViewModel : ViewModelBase
{
    private readonly VaultSession _session;

    public TotpPageViewModel(VaultSession session)
    {
        _session = session;
        Refresh();
    }

    public IReadOnlyList<TotpRowView> Rows { get; private set; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var previous = Rows.GroupBy(row => (row.EntryId, row.AccountId))
            .ToDictionary(group => group.Key, group => new Queue<TotpRowView>(group));
        var next = new List<TotpRowView>();
        var accounts = _session.Entries
            .SelectMany(entry => entry.Accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.TotpSecret))
                .Select(account => (EntryId: entry.Id, AccountId: account.Id, Name: VaultAudit.AccountTitle(entry, account), Secret: account.TotpSecret!)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (entryId, accountId, name, secret) in accounts)
        {
            TotpRowView? row = null;
            if (previous.TryGetValue((entryId, accountId), out var candidates))
            {
                candidates.TryDequeue(out row);
            }
            if (row is null || row.Name != name || row.Secret != secret)
            {
                row = new TotpRowView { EntryId = entryId, AccountId = accountId, Name = name, Secret = secret };
            }

            if (TotpGenerator.TryGenerate(secret, now, out var code, out var remaining))
            {
                if (TotpGenerator.TryParseSecret(secret, out var settings))
                {
                    row.Period = settings.PeriodSeconds;
                }
                row.Code = code;
                row.Remaining = remaining;
            }
            else
            {
                row.Code = "密钥无法识别";
                row.Remaining = 0;
            }

            next.Add(row);
        }

        // Keep existing cards during the countdown; only their numbers change.
        if (!Rows.SequenceEqual(next))
        {
            Rows = next;
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }
}
