namespace KeepPassword.Core.Vault;

/// <summary>按条目 UpdatedAt 合并两份已解密的保险库内容（零知识同步用）。</summary>
public static class VaultMerge
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public sealed record Result(
        IReadOnlyList<VaultEntry> Entries,
        IReadOnlyList<string> Spaces,
        IReadOnlyList<string> Tags,
        bool ClockSkewWarned,
        int LocalWins,
        int RemoteWins,
        int TiesKeptLocal);

    public static Result Merge(
        IReadOnlyList<VaultEntry> local,
        IReadOnlyList<VaultEntry> remote,
        IReadOnlyList<string>? localSpaces = null,
        IReadOnlyList<string>? localTags = null,
        IReadOnlyList<string>? remoteSpaces = null,
        IReadOnlyList<string>? remoteTags = null,
        DateTimeOffset? serverTime = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var clock = now ?? DateTimeOffset.UtcNow;
        var skew = serverTime is { } server && Math.Abs((clock - server).TotalSeconds) > MaxClockSkew.TotalSeconds;

        var map = new Dictionary<Guid, VaultEntry>();
        var localWins = 0;
        var remoteWins = 0;
        var ties = 0;

        foreach (var entry in local)
        {
            map[entry.Id] = Normalize(entry.Clone(), clock);
        }

        foreach (var entry in remote)
        {
            var incoming = Normalize(entry.Clone(), clock);
            if (!map.TryGetValue(incoming.Id, out var existing))
            {
                map[incoming.Id] = incoming;
                remoteWins++;
                continue;
            }

            var cmp = CompareRecency(existing, incoming);
            if (cmp < 0)
            {
                map[incoming.Id] = incoming;
                remoteWins++;
            }
            else if (cmp > 0)
            {
                localWins++;
            }
            else
            {
                ties++;
            }
        }

        var spaces = MergeLabels(localSpaces, remoteSpaces, map.Values.Where(e => !e.IsDeleted).Select(e => e.Space));
        var tags = MergeLabels(localTags, remoteTags,
            map.Values.Where(e => !e.IsDeleted).SelectMany(e => e.Tags));

        return new Result(map.Values.OrderBy(e => e.Id).ToList(), spaces, tags, skew, localWins, remoteWins, ties);
    }

    private static VaultEntry Normalize(VaultEntry entry, DateTimeOffset fallback)
    {
        if (entry.UpdatedAt == default)
        {
            entry.UpdatedAt = entry.DeletedAt ?? fallback;
        }

        if (entry.DeletedAt is { } deleted && deleted > entry.UpdatedAt)
        {
            entry.UpdatedAt = deleted;
        }

        return entry;
    }

    /// <summary>返回正数表示 left 更新，负数表示 right 更新，0 表示相同（保留 left）。</summary>
    public static int CompareRecency(VaultEntry left, VaultEntry right)
    {
        var leftAt = EffectiveTime(left);
        var rightAt = EffectiveTime(right);
        return leftAt.CompareTo(rightAt);
    }

    public static DateTimeOffset EffectiveTime(VaultEntry entry)
    {
        if (entry.UpdatedAt != default)
        {
            return entry.UpdatedAt;
        }

        return entry.DeletedAt ?? DateTimeOffset.MinValue;
    }

    private static IReadOnlyList<string> MergeLabels(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right,
        IEnumerable<string> fromEntries)
    {
        var result = new List<string>();
        foreach (var name in (left ?? []).Concat(right ?? []).Concat(fromEntries))
        {
            var normalized = LabelName.Normalize(name);
            if (normalized.Length == 0 || result.Contains(normalized, LabelName.Comparer))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }
}
