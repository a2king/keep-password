using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeepPassword.Core.Paths;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Messaging;

public sealed class AutofillChannel
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private AutofillChannel(int port, string token)
    {
        Port = port;
        Token = token;
    }

    public int Port { get; }

    public string Token { get; }

    public static string SessionFile()
    {
        var settings = CacheDirectory.SettingsFile();
        var directory = Path.GetDirectoryName(settings);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("无法确定本机填充通道的配置目录。");
        }

        return Path.Combine(directory, NativeMessagingDefaults.SessionFileName);
    }

    public static AutofillChannel Create(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return new AutofillChannel(port, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Publish(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new SessionDto { Port = Port, Token = Token }, Options);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        FileProtection.RestrictToCurrentUser(temp);
        File.Move(temp, path, overwrite: true);
        FileProtection.RestrictToCurrentUser(path);
    }

    public static bool TryRead(string path, out AutofillChannel? channel)
    {
        channel = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(path), Options);
            if (dto is null || dto.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(dto.Token))
            {
                return false;
            }

            channel = new AutofillChannel(dto.Port, dto.Token);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool TokenEquals(string? presented)
    {
        var actual = Encoding.UTF8.GetBytes(Token);
        var given = Encoding.UTF8.GetBytes(presented ?? "");
        var equal = actual.Length == given.Length && CryptographicOperations.FixedTimeEquals(actual, given);
        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(given);
        return equal;
    }

    public static string AttachToken(string json, string token)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("消息必须是 JSON 对象。");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("token"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("token", token);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class SessionDto
    {
        public int Port { get; set; }

        public string Token { get; set; } = "";
    }
}
