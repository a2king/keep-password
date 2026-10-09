using System.Text;

namespace KeepPassword.Core.Totp;

public static class Base32Encoding
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = new StringBuilder(text.Length);
        foreach (var current in text.Trim())
        {
            if (current is ' ' or '-' or '\t')
            {
                continue;
            }

            if (current == '=')
            {
                break;
            }

            cleaned.Append(char.ToUpperInvariant(current));
        }

        if (cleaned.Length == 0)
        {
            throw new FormatException("验证码密钥不是有效的 Base32。");
        }

        var buffer = 0;
        var bits = 0;
        var output = new List<byte>(cleaned.Length * 5 / 8);
        foreach (var current in cleaned.ToString())
        {
            var value = Alphabet.IndexOf(current);
            if (value < 0)
            {
                throw new FormatException("验证码密钥不是有效的 Base32。");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
