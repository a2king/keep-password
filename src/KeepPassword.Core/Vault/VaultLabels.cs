namespace KeepPassword.Core.Vault;

public static class LabelName
{
    public const int MaxLength = 40;

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxLength ? collapsed[..MaxLength] : collapsed;
    }

    public static string Require(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("请填写名称。", nameof(name));
        }

        return normalized;
    }

    public static List<string> NormalizeAll(IEnumerable<string>? names)
    {
        var result = new List<string>();
        if (names is null)
        {
            return result;
        }

        foreach (var name in names)
        {
            var normalized = Normalize(name);
            if (normalized.Length > 0 && !result.Contains(normalized, Comparer))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}

internal sealed class VaultLabels
{
    public const int MaxCount = 500;

    public List<string> Spaces { get; } = [];

    public List<string> Tags { get; } = [];

    public static VaultLabels From(IEnumerable<string>? spaces, IEnumerable<string>? tags, IEnumerable<VaultEntry> entries)
    {
        var labels = new VaultLabels();
        labels.Spaces.AddRange(LabelName.NormalizeAll(spaces).Take(MaxCount));
        labels.Tags.AddRange(LabelName.NormalizeAll(tags).Take(MaxCount));
        foreach (var entry in entries)
        {
            labels.Absorb(entry);
        }

        return labels;
    }

    public void Absorb(VaultEntry entry)
    {
        AddTo(Spaces, entry.Space);
        foreach (var tag in entry.Tags)
        {
            AddTo(Tags, tag);
        }
    }

    public static string AddTo(List<string> list, string? name)
    {
        var normalized = LabelName.Normalize(name);
        if (normalized.Length == 0)
        {
            return "";
        }

        var existing = list.FirstOrDefault(item => LabelName.Comparer.Equals(item, normalized));
        if (existing is not null)
        {
            return existing;
        }

        if (list.Count >= MaxCount)
        {
            throw new InvalidOperationException($"最多只能创建 {MaxCount} 个分类。");
        }

        list.Add(normalized);
        return normalized;
    }
}
