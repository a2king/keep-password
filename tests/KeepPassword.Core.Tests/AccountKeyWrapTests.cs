using System.Security.Cryptography;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class AccountKeyWrapTests
{
    [Fact]
    public void WrapRoundTrip_RejectsWrongPassword()
    {
        var key = RandomNumberGenerator.GetBytes(Argon2Id.HashLength);
        try
        {
            var blob = AccountKeyWrap.Wrap("account-password-12", key, KdfProfile.Fast);
            var opened = AccountKeyWrap.Unwrap("account-password-12", blob, KdfProfile.Fast);
            try
            {
                Assert.Equal(key, opened);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(opened);
            }

            Assert.Throws<CredentialRejectedException>(() =>
                AccountKeyWrap.Unwrap("wrong-password-12", blob, KdfProfile.Fast));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
