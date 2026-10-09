using System.Text;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Import;

public static class CsvImporter
{
    private static readonly string[] RequiredHeaders = ["name", "url", "username", "password", "note"];

    public static IReadOnlyList<VaultEntry> Import(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var delimiter = DetectDelimiter(text);
        var rows = Parse(text, delimiter);
        if (rows.Count == 0)
        {
            return [];
        }

        var columns = MapHeader(rows[0]);
        var entries = new List<VaultEntry>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            entries.Add(new VaultEntry
            {
                Id = Guid.NewGuid(),
                Name = Field(row, columns, "name"),
                Url = Field(row, columns, "url"),
                Username = Field(row, columns, "username"),
                Password = Field(row, columns, "password"),
                Note = Field(row, columns, "note")
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

        var missing = RequiredHeaders.Where(name => !map.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new FormatException("CSV 表头缺少列：" + string.Join("、", missing));
        }

        return map;
    }

    private static string Field(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string name)
    {
        var index = columns[name];
        return index < row.Count ? row[index] : "";
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
