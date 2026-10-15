using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class IndependentKeyTests
{
    [Fact]
    public void NewVaults_UseDifferentRandomKeys_WithTheSamePassword()
    {
        using var first = TestVault.CreateUnlocked(TestVault.NewPath());
        using var second = TestVault.CreateUnlocked(TestVault.NewPath());
        Assert.NotEqual(first.PendingEncryptionKey, second.PendingEncryptionKey);
        var json = Read(first.VaultPath);
        Assert.Equal(2, (int)json["version"]!);
        Assert.DoesNotContain(first.PendingEncryptionKey[4..], json.ToJsonString());
        var derived = Argon2Id.Derive("correct horse", KdfProfile.Fast,
            Convert.FromBase64String((string)json["kdf"]!["salt"]!));
        var rawKey = Convert.FromBase64String(first.PendingEncryptionKey[4..]);
        Assert.False(CryptographicOperations.FixedTimeEquals(derived, rawKey));
        // The password-derived key can unwrap the random key, but cannot decrypt the payload.
        var wrap = json["passwordWrap"]!;
        var unwrapped = Decrypt(derived, wrap, "keep-password/password-wrap/v2|ada");
        Assert.Equal(rawKey, unwrapped);
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(derived, json["cipher"]!, "keep-password/payload/v2|ada"));
    }

    [Fact]
    public void KeyFile_PersistsAcrossRestartAndPasswordChanges_WithoutReencryptingPayload()
    {
        var path = TestVault.NewPath();
        string key;
        using (var created = TestVault.CreateUnlocked(path))
        {
            created.Upsert(Entry("original"));
            created.Save();
            key = created.PendingEncryptionKey;
        }
        using var session = TestVault.Store().Unlock(path, "correct horse");
        Assert.True(session.NeedsKeyFileSave);
        Assert.Equal(key, session.PendingEncryptionKey);
        var keyPath = path + ".kpkey";
        session.SaveEncryptionKeyFile(keyPath, key);
        Assert.Equal(key, VaultKeyFile.Read(keyPath));
        Assert.False(session.NeedsKeyFileSave);
        Assert.Throws<InvalidOperationException>(() => session.PendingEncryptionKey);
        var before = Read(path);
        session.ChangeMasterPassword("correct horse", "changed-password");
        var after = Read(path);
        Assert.Equal(before["cipher"]!.ToJsonString(), after["cipher"]!.ToJsonString());
        Assert.NotEqual(before["passwordWrap"]!.ToJsonString(), after["passwordWrap"]!.ToJsonString());
        Assert.NotEqual(before["kdf"]!["salt"]!.ToString(), after["kdf"]!["salt"]!.ToString());
        Assert.Throws<UnlockFailedException>(() => TestVault.Store().Unlock(path, "correct horse"));
        using var reloaded = TestVault.Store().Unlock(path, "changed-password");
        Assert.False(reloaded.NeedsKeyFileSave);
        reloaded.VerifyEncryptionKey(VaultKeyFile.Read(keyPath));
        Assert.Equal("original", Assert.Single(reloaded.Entries).Name);
        using var quick = TestVault.Store().UnlockWithShortKey(path, "short-key");
        quick.VerifyEncryptionKey(key);
    }

    [Fact]
    public void BackupAndCsv_RequireMatchingEncryptionKey_AfterMultiplePasswordChanges()
    {
        var path = TestVault.NewPath();
        var backup = TestVault.NewPath();
        using var source = TestVault.CreateUnlocked(path);
        using var target = TestVault.CreateUnlocked(TestVault.NewPath());
        source.Upsert(Entry("source"));
        source.Save();
        target.Upsert(Entry("target"));
        target.Save();
        var key = source.PendingEncryptionKey;
        var targetKey = target.PendingEncryptionKey;
        source.SaveEncryptionKeyFile(path + ".kpkey", key);
        EncryptedBackup.Export(path, backup);
        Assert.DoesNotContain("passwordWrap", File.ReadAllText(backup));
        Assert.DoesNotContain("shortKey", File.ReadAllText(backup));
        Assert.Throws<InvalidDataException>(() => TestVault.Store().Unlock(backup, "correct horse"));
        source.ChangeMasterPassword("correct horse", "second-password");
        source.ChangeMasterPassword("second-password", "third-password");
        Assert.Throws<CredentialRejectedException>(() => source.ExportCsv("third-password"));
        Assert.Throws<CredentialRejectedException>(() => source.ExportCsv(targetKey));
        Assert.Contains("secret-original", source.ExportCsv(key));
        Assert.Throws<CredentialRejectedException>(() => target.RestoreBackup(backup, targetKey));
        Assert.Equal("target", Assert.Single(target.Entries).Name);
        Assert.Throws<CredentialRejectedException>(() => EncryptedBackup.ReadEntries(backup, "correct horse"));
        Assert.Equal(1, target.RestoreBackup(backup, key));
        Assert.Equal("source", Assert.Single(target.Entries).Name);
        target.VerifyEncryptionKey(targetKey);
        Assert.Throws<CredentialRejectedException>(() => target.VerifyEncryptionKey(key));
        Assert.False(File.Exists(backup + ".quick")); // Restore does not enable a password/short-key login side effect.
        using var reloaded = TestVault.Store().Unlock(target.VaultPath, "correct horse");
        Assert.Equal("source", Assert.Single(reloaded.Entries).Name);
    }

    [Fact]
    public void KeyRecovery_PreservesPayloadAndKey_RejectsWrongKeyWithoutChangingFile()
    {
        var path = TestVault.NewPath();
        using var created = TestVault.CreateUnlocked(path);
        created.Upsert(Entry("retained"));
        created.Save();
        var key = created.PendingEncryptionKey;
        created.SaveEncryptionKeyFile(path + ".kpkey", key);
        var before = File.ReadAllText(path);
        using var other = TestVault.CreateUnlocked(TestVault.NewPath());
        Assert.Throws<CredentialRejectedException>(() => TestVault.Store().RecoverWithKey(path, other.PendingEncryptionKey, "reset-password"));
        Assert.Equal(before, File.ReadAllText(path));
        using var recovered = TestVault.Store().RecoverWithKey(path, key, "reset-password");
        Assert.Equal(JsonNode.Parse(before)!["cipher"]!.ToJsonString(), Read(path)["cipher"]!.ToJsonString());
        recovered.VerifyEncryptionKey(key);
        Assert.False(recovered.NeedsKeyFileSave);
        Assert.False(TestVault.Store().HasShortUnlock(path));
        Assert.Throws<UnlockFailedException>(() => TestVault.Store().Unlock(path, "correct horse"));
        using var unlocked = TestVault.Store().Unlock(path, "reset-password");
        Assert.Equal("retained", Assert.Single(unlocked.Entries).Name);
    }

    [Fact]
    public void FailedKeySave_LeavesSetupPending_AndDoesNotOverwriteVault()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var key = session.PendingEncryptionKey;
        var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => session.SaveEncryptionKeyFile(path, key));
        var blockedPath = path + ".blocked.kpkey";
        Directory.CreateDirectory(blockedPath);
        Assert.True(Record.Exception(() => session.SaveEncryptionKeyFile(blockedPath, key)) is IOException or UnauthorizedAccessException);
        Assert.True(session.NeedsKeyFileSave);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(blockedPath) + ".*.tmp"));
        using var reloaded = TestVault.Store().Unlock(path, "correct horse");
        Assert.True(reloaded.NeedsKeyFileSave);
        Assert.Equal(key, reloaded.PendingEncryptionKey);
    }

    [Fact]
    public void SavingKey_CannotOverwriteAnotherVaultsPermanentKey()
    {
        using var first = TestVault.CreateUnlocked(TestVault.NewPath());
        using var second = TestVault.CreateUnlocked(TestVault.NewPath());
        var keyPath = first.VaultPath + ".kpkey";
        var original = first.PendingEncryptionKey;
        first.SaveEncryptionKeyFile(keyPath, original);
        Assert.Throws<IOException>(() => second.SaveEncryptionKeyFile(keyPath, second.PendingEncryptionKey));
        Assert.Equal(original, VaultKeyFile.Read(keyPath));
        Assert.True(second.NeedsKeyFileSave);
        first.SaveEncryptionKeyFile(keyPath, original); // Saving the same key is idempotent.
    }

    [Fact]
    public void FailedPasswordChange_LeavesOriginalCredentialsAndKeyUsable()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var key = session.PendingEncryptionKey;
        var before = File.ReadAllBytes(path);
        Assert.Throws<CredentialRejectedException>(() => session.ChangeMasterPassword("wrong-password", "next-password"));
        Assert.Equal(before, File.ReadAllBytes(path));
        session.Relocate(path + ".missing");
        Assert.Throws<FileNotFoundException>(() => session.ChangeMasterPassword("correct horse", "next-password"));
        session.Relocate(path);
        session.VerifyEncryptionKey(key);
        session.ChangeMasterPassword("correct horse", "next-password");
        using var reopened = TestVault.Store().Unlock(path, "next-password");
        reopened.VerifyEncryptionKey(key);
    }

    [Fact]
    public void FailedRestore_RollsBackEntriesAndLabels()
    {
        using var source = TestVault.CreateUnlocked(TestVault.NewPath());
        source.Upsert(Entry("source"));
        source.Save();
        using var target = TestVault.CreateUnlocked(TestVault.NewPath());
        target.Upsert(Entry("target"));
        target.AddTag("original-tag");
        target.Save();
        var blockedPath = TestVault.NewPath();
        Directory.CreateDirectory(blockedPath);
        target.Relocate(blockedPath);
        Assert.True(Record.Exception(() => target.RestoreBackup(source.VaultPath, source.PendingEncryptionKey)) is IOException or UnauthorizedAccessException);
        Assert.Equal("target", Assert.Single(target.Entries).Name);
        Assert.Contains("original-tag", target.Tags);
    }

    [Fact]
    public void LegacyVault_UpgradesOnceOnMasterChange_ThenKeepsItsRandomKey()
    {
        var path = CreateLegacyVault();
        using var session = TestVault.Store().Unlock(path, "correct horse");
        Assert.False(session.UsesIndependentKey);
        Assert.Throws<InvalidOperationException>(() => session.ExportCsv("correct horse"));
        session.EnableShortUnlock("short-key");
        session.ChangeMasterPassword("correct horse", "new-password");
        Assert.True(session.UsesIndependentKey);
        Assert.True(session.NeedsKeyFileSave);
        var key = session.PendingEncryptionKey;
        var upgraded = Read(path)["cipher"]!.ToJsonString();
        session.ChangeMasterPassword("new-password", "final-password");
        Assert.Equal(upgraded, Read(path)["cipher"]!.ToJsonString());
        Assert.Equal(key, session.PendingEncryptionKey);
        using var master = TestVault.Store().Unlock(path, "final-password");
        using var quick = TestVault.Store().UnlockWithShortKey(path, "short-key");
        Assert.Equal("legacy-entry", Assert.Single(master.Entries).Name);
        quick.VerifyEncryptionKey(key);
    }

    [Theory]
    [InlineData("passwordWrap")]
    [InlineData("cipher")]
    public void CorruptedCipherOrWrap_IsRejected(string part)
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var json = Read(path);
        var bytes = Convert.FromBase64String((string)json[part]!["tag"]!);
        bytes[0] ^= 1;
        json[part]!["tag"] = Convert.ToBase64String(bytes);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<UnlockFailedException>(() => TestVault.Store().Unlock(path, "correct horse"));
    }

    [Fact]
    public void LockedSession_CannotRevealSaveExportOrVerifyKey()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var key = session.PendingEncryptionKey;
        session.Lock();
        Assert.Throws<InvalidOperationException>(() => session.PendingEncryptionKey);
        Assert.Throws<InvalidOperationException>(() => session.ExportCsv(key));
        Assert.Throws<InvalidOperationException>(() => session.SaveEncryptionKeyFile(session.VaultPath + ".kpkey", key));
        Assert.Throws<InvalidOperationException>(() => session.VerifyEncryptionKey(key));
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static VaultEntry Entry(string name) => new()
    {
        Name = name, Accounts = [new VaultAccount { Username = "test-user", Password = "secret-original" }]
    };

    private static byte[] Decrypt(byte[] key, JsonNode cipher, string aad) => VaultCipher.Decrypt(key,
        Convert.FromBase64String((string)cipher["nonce"]!), Convert.FromBase64String((string)cipher["ciphertext"]!),
        Convert.FromBase64String((string)cipher["tag"]!), Encoding.UTF8.GetBytes(aad));

    internal static string CreateLegacyVault()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        session.Upsert(Entry("legacy-entry"));
        session.Save();
        var json = Read(path);
        var dataKey = Convert.FromBase64String(session.PendingEncryptionKey[4..]);
        var plaintext = Decrypt(dataKey, json["cipher"]!, "keep-password/payload/v2|ada");
        var passwordKey = Argon2Id.Derive("correct horse", KdfProfile.Fast, Convert.FromBase64String((string)json["kdf"]!["salt"]!));
        var cipher = VaultCipher.Encrypt(passwordKey, plaintext, VaultCipher.AssociatedData("ada"));
        json["version"] = 1;
        json.Remove("passwordWrap");
        json.Remove("keyFileSaved");
        json["cipher"]!["nonce"] = Convert.ToBase64String(cipher.Nonce);
        json["cipher"]!["ciphertext"] = Convert.ToBase64String(cipher.Ciphertext);
        json["cipher"]!["tag"] = Convert.ToBase64String(cipher.Tag);
        File.WriteAllText(path, json.ToJsonString());
        File.Delete(path + ".quick");
        CryptographicOperations.ZeroMemory(dataKey);
        CryptographicOperations.ZeroMemory(passwordKey);
        CryptographicOperations.ZeroMemory(plaintext);
        return path;
    }
}
