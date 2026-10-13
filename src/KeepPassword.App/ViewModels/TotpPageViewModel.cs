using KeepPassword.Core.Security;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class TotpRowView : ViewModelBase
{
    private string _code = "";
    private int _remaining;

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
                OnPropertyChanged(nameof(RemainingText));
            }
        }
    }

    public string RemainingText => Remaining > 0 ? $"{Remaining} 秒" : "";
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
        var previous = Rows.ToDictionary(row => row.Name + "|" + row.Secret, row => row);
        var next = new List<TotpRowView>();
        var accounts = _session.Entries
            .SelectMany(entry => entry.Accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.TotpSecret))
                .Select(account => (Name: VaultAudit.AccountTitle(entry, account), Secret: account.TotpSecret!)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, secret) in accounts)
        {
            var key = name + "|" + secret;
            if (!previous.TryGetValue(key, out var row))
            {
                row = new TotpRowView { Name = name, Secret = secret };
            }

            if (TotpGenerator.TryGenerate(secret, now, out var code, out var remaining))
            {
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

        Rows = next;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
