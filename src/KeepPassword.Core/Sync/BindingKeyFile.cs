using System.Text.Json;

namespace KeepPassword.Core.Sync;

public static class BindingKeyFile
{
    public static string Parse(string text)
    {
        var value = text.Trim();
        if (value.Length is 0 or > 8192)
        {
            throw new InvalidDataException("请选择 Keep Password 绑定密钥文件（.kpbind）。");
        }

        if (value.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(value);
                var root = json.RootElement;
                if (root.GetProperty("format").GetString() != "keep-password-binding"
                    || root.GetProperty("version").GetInt32() != 1)
                {
                    throw new FormatException();
                }

                value = root.GetProperty("key").GetString()?.Trim() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("绑定密钥文件格式不正确。");
            }
        }

        if (!value.StartsWith("KPB-", StringComparison.Ordinal) || value.Length < 20)
        {
            throw new InvalidDataException("绑定密钥不正确。");
        }

        return value;
    }

    public static string Read(string path)
    {
        if (new FileInfo(path).Length > 8192)
        {
            throw new InvalidDataException("请选择 Keep Password 绑定密钥文件（.kpbind）。");
        }

        return Parse(File.ReadAllText(path));
    }
}
