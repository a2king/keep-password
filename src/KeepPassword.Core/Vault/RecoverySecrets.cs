using System.Security.Cryptography;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public static class RecoveryRisk
{
    public const string Warning =
        "安全问题远弱于主密码。知道答案的人可以重置保险库并读取全部内容。这不能代替主密码，也不会提高安全性。只有在你明确接受该风险时才应启用。";
}

public sealed class RecoverySetup
{
    public required bool RiskAcknowledged { get; init; }

    public required IReadOnlyList<string> Questions { get; init; }

    public required IReadOnlyList<string> Answers { get; init; }
}

internal sealed class RecoverySecrets
{
    public required KdfProfile Profile { get; init; }

    public required byte[] Salt { get; init; }

    public required string[] Questions { get; init; }

    public required byte[] Key { get; init; }

    public static RecoverySecrets Create(RecoverySetup setup, KdfProfile profile)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (!setup.RiskAcknowledged)
        {
            throw new ArgumentException(RecoveryRisk.Warning, nameof(setup));
        }

        if (setup.Questions.Count != 3 || setup.Answers.Count != 3)
        {
            throw new ArgumentException("请填写三个安全问题和答案。", nameof(setup));
        }

        var questions = setup.Questions.Select(question => question.Trim()).ToArray();
        if (questions.Any(question => question.Length is < 1 or > 200))
        {
            throw new ArgumentException("安全问题不能为空，且不能超过 200 个字符。", nameof(setup));
        }

        var answers = setup.Answers.Select(Normalize).ToArray();
        if (answers.Any(answer => answer.Length == 0))
        {
            throw new ArgumentException("安全问题答案不能为空。", nameof(setup));
        }

        var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
        return new RecoverySecrets
        {
            Profile = profile,
            Salt = salt,
            Questions = questions,
            Key = Argon2Id.Derive(Secret(answers), profile, salt)
        };
    }

    public static string Secret(IReadOnlyList<string> answers) =>
        string.Join('\n', answers.Select(Normalize));

    public static string Normalize(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "";
        }

        var parts = answer.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    public void Zero()
    {
        if (Key.Length > 0)
        {
            CryptographicOperations.ZeroMemory(Key);
        }
    }
}
