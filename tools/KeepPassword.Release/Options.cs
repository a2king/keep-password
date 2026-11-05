namespace KeepPassword.Release;

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var result = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数：{arg}");
            }

            var name = arg[2..];
            if (name == "no-password")
            {
                result._flags.Add(name);
            }
            else if (i + 1 < list.Count)
            {
                result._values[name] = list[++i];
            }
            else
            {
                throw new ArgumentException($"参数 --{name} 缺少值。");
            }
        }

        return result;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"缺少必填参数 --{name}。");
}
