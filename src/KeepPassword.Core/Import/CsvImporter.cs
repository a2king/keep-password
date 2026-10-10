using System.Text;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Import;

public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

public static class CsvImporter
{
    public const int MaxCharacters = 8_000_000;
    public const int MaxRows = 10_000;

    private static readonly (string Key, string[] Aliases, bool Required)[] Columns =
    [
        ("name", ["name", "title"], true),
        ("url", ["url", "website"], true),
        ("username", ["username"], true),
        ("password", ["password"], true),
        ("note", ["note", "notes"], false),
        ("otp", ["otpauth", "otp", "onetimepassword"], false)
    ];

    public static CsvTable ReadTable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters)
        {
            throw new FormatException("CSV 文件过大。");
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new CsvTable([], []);
        }

        var rows = Parse(text, DetectDelimiter(text));
        if (rows.Count > MaxRows + 1)
        {
            throw new FormatException("CSV 行数超过上限。");
        }

        if (rows.Count == 0)
        {
            return new CsvTable([], []);
        }

        var header = rows[0].Select(name => name.Trim()).ToList();
        var body = rows.Skip(1)
            .Where(row => !row.All(string.IsNullOrWhiteSpace))
            .Select(row => (IReadOnlyList<string>)row)
            .ToList();
        return new CsvTable(header, body);
    }

    public static IReadOnlyList<VaultEntry> Import(string text)
    {
        var table = ReadTable(text);
        if (table.Headers.Count == 0)
        {
            return [];
        }

        var columns = MapHeader(table.Headers);
        var entries = new List<VaultEntry>();
        foreach (var row in table.Rows)
        {
            entries.Add(new VaultEntry
            {
                Id = Guid.NewGuid(),
                Name = Field(row, columns, "name"),
                Url = Field(row, columns, "url"),
                Username = Field(row, columns, "username"),
                Password = Field(row, columns, "password"),
                Note = Field(row, columns, "note"),
                TotpSecret = TotpSecret(Field(row, columns, "otp"))
            });
        }

        return entries;
    }

    private static char DetectDelimiter(string text)
    {
        var inQuotes = false;
        var commas = 0;
        var tabs = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (current is '\n' or '\r')
            {
                break;
            }

            if (current == ',')
            {
                commas++;
            }
            else if (current == '\t')
            {
                tabs++;
            }
        }

        return tabs > 0 && tabs >= commas ? '\t' : ',';
    }

    private static Dictionary<string, int> MapHeader(IReadOnlyList<string> header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Length > 0 && !map.ContainsKey(name))
            {
                map[name] = i;
            }
        }

        var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var (key, aliases, required) in Columns)
        {
            var found = aliases.FirstOrDefault(map.ContainsKey);
            if (found is null)
            {
                if (required)
                {
                    missing.Add(key);
                }

                continue;
            }

            resolved[key] = map[found];
        }

        if (missing.Count > 0)
        {
            throw new FormatException("CSV 表头缺少列：" + string.Join("、", missing));
        }

        return resolved;
    }

    internal static string? TotpSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var secretIndex = value.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
        if (secretIndex < 0)
        {
            return value.Trim();
        }

        var secret = value[(secretIndex + "secret=".Length)..];
        var end = secret.IndexOfAny(['&', ' ', '\t']);
        if (end >= 0)
        {
            secret = secret[..end];
        }

        return string.IsNullOrWhiteSpace(secret) ? null : Uri.UnescapeDataString(secret);
    }

    private static string Field(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out var index) || index >= row.Count)
        {
            return "";
        }

        return row[index];
    }

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(current);
                }

                continue;
            }

            if (current == '"')
            {
                inQuotes = true;
                continue;
            }

            if (current == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (current == '\r')
            {
                continue;
            }

            if (current == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                continue;
            }

            field.Append(current);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
