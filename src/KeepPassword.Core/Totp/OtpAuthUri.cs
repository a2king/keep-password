namespace KeepPassword.Core.Totp;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512
}

public sealed record TotpSettings(
    string Secret,
    int Digits = TotpGenerator.Digits,
    int PeriodSeconds = TotpGenerator.PeriodSeconds,
    TotpAlgorithm Algorithm = TotpAlgorithm.Sha1,
    string Issuer = "",
    string Account = "")
{
    public bool IsDefault => Digits == TotpGenerator.Digits
        && PeriodSeconds == TotpGenerator.PeriodSeconds
        && Algorithm == TotpAlgorithm.Sha1;

    public string DisplayName => (Issuer.Length, Account.Length) switch
    {
        (> 0, > 0) => $"{Issuer} · {Account}",
        (> 0, _) => Issuer,
        (_, > 0) => Account,
        _ => "未命名验证码"
    };
}

/// <summary>
/// 解析 Google Authenticator Key URI：otpauth://totp/Issuer:account?secret=...&amp;issuer=...&amp;digits=6&amp;period=30&amp;algorithm=SHA1。
/// </summary>
public static class OtpAuthUri
{
    private const string Prefix = "otpauth://";

    public static bool IsOtpAuth(string? text) =>
        text is not null && text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out TotpSettings settings)
    {
        settings = new TotpSettings("");
        if (!IsOtpAuth(text))
        {
            return false;
        }

        var rest = text!.Trim()[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0 || !rest[..slash].Equals("totp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = rest[(slash + 1)..];
        var question = rest.IndexOf('?');
        var label = Unescape(question < 0 ? rest : rest[..question], plusIsSpace: false);
        var query = question < 0 ? "" : rest[(question + 1)..];

        var issuer = "";
        var account = label.Trim();
        var colon = label.IndexOf(':');
        if (colon >= 0)
        {
            issuer = label[..colon].Trim();
            account = label[(colon + 1)..].Trim();
        }

        string? secret = null;
        var digits = TotpGenerator.Digits;
        var period = TotpGenerator.PeriodSeconds;
        var algorithm = TotpAlgorithm.Sha1;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = (equals < 0 ? pair : pair[..equals]).Trim().ToLowerInvariant();
            var value = equals < 0 ? "" : Unescape(pair[(equals + 1)..], plusIsSpace: true).Trim();
            switch (key)
            {
                case "secret":
                    secret = value.Replace(" ", "", StringComparison.Ordinal);
                    break;
                case "issuer" when value.Length > 0:
                    issuer = value;
                    break;
                case "digits":
                    if (!int.TryParse(value, out digits) || digits is < 6 or > 8)
                    {
                        return false;
                    }

                    break;
                case "period":
                    if (!int.TryParse(value, out period) || period is < 1 or > 3600)
                    {
                        return false;
                    }

                    break;
                case "algorithm":
                    switch (value.ToUpperInvariant())
                    {
                        case "SHA1":
                            algorithm = TotpAlgorithm.Sha1;
                            break;
                        case "SHA256":
                            algorithm = TotpAlgorithm.Sha256;
                            break;
                        case "SHA512":
                            algorithm = TotpAlgorithm.Sha512;
                            break;
                        default:
                            return false;
                    }

                    break;
            }
        }

        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        try
        {
            if (Base32Encoding.Decode(secret).Length == 0)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        settings = new TotpSettings(secret.ToUpperInvariant(), digits, period, algorithm, issuer, account);
        return true;
    }

    private static string Unescape(string value, bool plusIsSpace)
    {
        if (plusIsSpace)
        {
            value = value.Replace('+', ' ');
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
