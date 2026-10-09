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
        foreach (var entry in _session.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.TotpSecret))
                     .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = entry.Name + "|" + entry.TotpSecret;
            if (!previous.TryGetValue(key, out var row))
            {
                row = new TotpRowView { Name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name, Secret = entry.TotpSecret };
            }

            if (TotpGenerator.TryGenerateFromBase32(entry.TotpSecret, now, out var code))
            {
                row.Code = code;
                row.Remaining = TotpGenerator.RemainingSeconds(now);
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
