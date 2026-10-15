using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

/// <summary>
/// Stores only browser autofill metadata so a locked desktop session can still
/// show candidates. Windows DPAPI keeps the index tied to the current user;
/// passwords and other secret fields never enter this file.
/// </summary>
public static class AutofillIndexStore
{
    private const string Suffix = ".autofill-index";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("keep-password/autofill-index/v1");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(string vaultPath, IEnumerable<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);
        ArgumentNullException.ThrowIfNull(entries);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var candidates = AutofillMatcher.Catalog(entries);
        var json = JsonSerializer.SerializeToUtf8Bytes(new IndexDto
        {
            Version = 1,
            Candidates = candidates.ToList()
        }, Options);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            var path = IndexPath(vaultPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            FileProtection.RestrictToCurrentUser(temp);
            File.Move(temp, path, overwrite: true);
            FileProtection.RestrictToCurrentUser(path);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The index is an optional convenience cache. A vault save must not
            // fail just because the platform cannot protect the cache.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public static bool TryRead(string vaultPath, out IReadOnlyList<AutofillCandidate> candidates)
    {
        candidates = [];
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var path = IndexPath(vaultPath);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var stored = File.ReadAllBytes(path);
            var json = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var dto = JsonSerializer.Deserialize<IndexDto>(json, Options);
                if (dto is null || dto.Version != 1 || dto.Candidates is null)
                {
                    return false;
                }

                candidates = dto.Candidates
                    .Where(IsValid)
                    .ToList();
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
                CryptographicOperations.ZeroMemory(stored);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static IReadOnlyList<AutofillCandidate> Match(
        string vaultPath,
        AutofillRequest request,
        out bool indexAvailable)
    {
        indexAvailable = TryRead(vaultPath, out var candidates);
        return indexAvailable ? AutofillMatcher.MatchCatalog(candidates, request) : [];
    }

    private static bool IsValid(AutofillCandidate? candidate) =>
        candidate is not null
        && candidate.Id != Guid.Empty
        && candidate.AccountId != Guid.Empty
        && !string.IsNullOrWhiteSpace(candidate.Domain)
        && candidate.Name is not null
        && !string.IsNullOrEmpty(candidate.Username);

    private static string IndexPath(string vaultPath) => vaultPath + Suffix;

    private sealed class IndexDto
    {
        public int Version { get; set; }

        public List<AutofillCandidate> Candidates { get; set; } = [];
    }
}
