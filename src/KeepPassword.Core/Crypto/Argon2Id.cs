using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace KeepPassword.Core.Crypto;

public static class Argon2Id
{
    public const int SaltLength = 16;
    public const int HashLength = 32;

    public static byte[] Derive(string password, KdfProfile profile, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        profile.Validate();
        if (salt.Length < 8)
        {
            throw new ArgumentException("盐长度不足。", nameof(salt));
        }

        var generator = new Argon2BytesGenerator();
        generator.Init(new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithIterations(profile.Iterations)
            .WithMemoryAsKB(profile.MemoryKb)
            .WithParallelism(profile.Parallelism)
            .WithSalt(salt)
            .Build());

        var output = new byte[HashLength];
        generator.GenerateBytes(Encoding.UTF8.GetBytes(password), output);
        return output;
    }

    public static byte[] Hash(string secret, KdfProfile profile, byte[] salt) =>
        Derive(secret, profile, salt);

    public static bool Verify(string secret, KdfProfile profile, byte[] salt, byte[] expected)
    {
        var actual = Derive(secret, profile, salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }
}
