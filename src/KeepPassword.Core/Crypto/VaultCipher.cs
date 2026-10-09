using System.Security.Cryptography;
using System.Text;

namespace KeepPassword.Core.Crypto;

public static class VaultCipher
{
    public const int NonceLength = 12;
    public const int TagLength = 16;

    public static byte[] AssociatedData(string account) =>
        Encoding.UTF8.GetBytes("keep-password/v1|" + account);

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return (nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] associatedData)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}
