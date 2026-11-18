namespace KeepPassword.Core.Vault;

public enum MergeSide
{
    Local,
    Remote
}

/// <summary>同一条目本机和云端内容不同。只含名称和差异项名称，不含密码等内容。</summary>
public sealed record MergeConflict(
    Guid Id,
    string LocalName,
    string RemoteName,
    VaultItemKind Kind,
    DateTimeOffset LocalUpdatedAt,
    DateTimeOffset RemoteUpdatedAt,
    IReadOnlyList<string> Differences)
{
    /// <summary>不选时的默认结果：较新的一边（时间相同保留本机）。</summary>
    public MergeSide Suggested => LocalUpdatedAt >= RemoteUpdatedAt ? MergeSide.Local : MergeSide.Remote;
}

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
        DateTimeOffset? now = null,
        IReadOnlyDictionary<Guid, MergeSide>? choices = null)
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

            // 用户逐条选过的冲突按选择取舍，其余按时间。
            var cmp = choices is not null && choices.TryGetValue(incoming.Id, out var side)
                ? (side == MergeSide.Remote ? -1 : 1)
                : CompareRecency(existing, incoming);
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

    /// <summary>同一条目两边内容不同。只返回编号，时间戳不同但内容相同不算冲突。</summary>
    public static IReadOnlyList<Guid> ContentConflicts(IReadOnlyList<VaultEntry> local, IReadOnlyList<VaultEntry> remote)
    {
        var remoteById = remote.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        var conflicts = new List<Guid>();
        foreach (var entry in local)
        {
            if (remoteById.TryGetValue(entry.Id, out var other) && !SameContent(entry, other))
            {
                conflicts.Add(entry.Id);
            }
        }

        return conflicts;
    }

    /// <summary>冲突详情，供界面逐条选择。</summary>
    public static IReadOnlyList<MergeConflict> DescribeConflicts(IReadOnlyList<VaultEntry> local, IReadOnlyList<VaultEntry> remote)
    {
        var remoteById = remote.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        var result = new List<MergeConflict>();
        foreach (var entry in local)
        {
            if (!remoteById.TryGetValue(entry.Id, out var other) || SameContent(entry, other))
            {
                continue;
            }

            result.Add(new MergeConflict(entry.Id, entry.Name, other.Name, entry.Kind,
                EffectiveTime(entry), EffectiveTime(other), Differences(entry, other)));
        }

        return result;
    }

    private static IReadOnlyList<string> Differences(VaultEntry left, VaultEntry right)
    {
        if (left.IsDeleted != right.IsDeleted)
        {
            return [left.IsDeleted ? "本机已删除" : "云端已删除"];
        }

        var list = new List<string>();
        if (left.Name != right.Name) list.Add("名称");
        if (left.Url != right.Url) list.Add("网址");
        if (left.Note != right.Note) list.Add("备注");
        if (left.Space != right.Space || !left.Tags.SequenceEqual(right.Tags)) list.Add("空间/标签");
        if (left.Favorite != right.Favorite) list.Add("收藏");
        if (left.Fields.Count != right.Fields.Count
            || left.Fields.Any(pair => !right.Fields.TryGetValue(pair.Key, out var value) || value != pair.Value)
            || left.SshKeyId != right.SshKeyId)
        {
            list.Add("连接字段");
        }

        if (left.Accounts.Count != right.Accounts.Count
            || left.Accounts.Zip(right.Accounts).Any(pair => pair.First.Username != pair.Second.Username || pair.First.Note != pair.Second.Note || pair.First.Auth != pair.Second.Auth))
        {
            list.Add("账号");
        }

        if (left.Accounts.Zip(right.Accounts).Any(pair => pair.First.Password != pair.Second.Password || pair.First.TotpSecret != pair.Second.TotpSecret))
        {
            list.Add("密码/验证码");
        }

        if (left.CustomFields.Count != right.CustomFields.Count
            || left.CustomFields.Zip(right.CustomFields).Any(pair => pair.First.Name != pair.Second.Name || pair.First.Value != pair.Second.Value))
        {
            list.Add("自定义字段");
        }

        if (left.Nodes.Count != right.Nodes.Count
            || left.Nodes.Zip(right.Nodes).Any(pair => pair.First.Host != pair.Second.Host || pair.First.Name != pair.Second.Name))
        {
            list.Add("节点");
        }

        if (left.Kind != right.Kind) list.Add("类型");
        return list.Count == 0 ? ["内容"] : list;
    }

    private static bool SameContent(VaultEntry left, VaultEntry right)
    {
        if (left.IsDeleted || right.IsDeleted)
        {
            return left.IsDeleted == right.IsDeleted;
        }

        return left.Kind == right.Kind
            && left.Favorite == right.Favorite
            && left.Name == right.Name
            && left.Url == right.Url
            && left.Note == right.Note
            && left.Space == right.Space
            && left.SshKeyId == right.SshKeyId
            && left.Tags.SequenceEqual(right.Tags)
            && left.Fields.Count == right.Fields.Count
            && left.Fields.All(pair => right.Fields.TryGetValue(pair.Key, out var value) && value == pair.Value)
            && left.CustomFields.Count == right.CustomFields.Count
            && left.CustomFields.Zip(right.CustomFields).All(pair => pair.First.Name == pair.Second.Name && pair.First.Value == pair.Second.Value)
            && left.Accounts.Count == right.Accounts.Count
            && left.Accounts.Zip(right.Accounts).All(pair =>
                pair.First.Username == pair.Second.Username
                && pair.First.Password == pair.Second.Password
                && pair.First.Note == pair.Second.Note
                && pair.First.TotpSecret == pair.Second.TotpSecret
                && pair.First.Auth == pair.Second.Auth)
            && left.Nodes.Count == right.Nodes.Count
            && left.Nodes.Zip(right.Nodes).All(pair => pair.First.Host == pair.Second.Host && pair.First.Name == pair.Second.Name);
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
