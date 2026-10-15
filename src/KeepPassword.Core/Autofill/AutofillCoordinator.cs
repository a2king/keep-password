using KeepPassword.Core.Messaging;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

public static class AutofillCoordinator
{
    public static AutofillResponse FillCandidateFromLockedVault(VaultStore store, string vaultPath, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Origin != AutofillOrigin.Browser || request.EntryId is null || request.AccountId is null)
        {
            return AutofillResponse.Error("未选择账号。");
        }

        if (string.IsNullOrEmpty(request.ShortKey))
        {
            return AutofillResponse.Error("请填写短密钥。");
        }

        try
        {
            // Never attach this temporary session to the desktop UI. Dispose
            // clears the decrypted vault as soon as the selected fill is ready.
            using var temporary = store.UnlockWithShortKey(vaultPath, request.ShortKey);
            return FillCandidate(temporary, request);
        }
        catch (UnlockFailedException)
        {
            return AutofillResponse.Error("短密钥不正确。");
        }
        catch (InvalidOperationException ex)
        {
            return AutofillResponse.Error(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return AutofillResponse.Error("保险库文件无法读取。");
        }
    }

    public static AutofillResponse DiscoverCandidates(VaultSession? session, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (session is null || !session.IsUnlocked)
        {
            return AutofillResponse.Locked();
        }

        var matches = AutofillMatcher.Match(session.Entries, request);
        return matches.Count == 0 ? AutofillResponse.NoMatch() : AutofillResponse.CandidatesList(matches);
    }

    public static AutofillResponse FillCandidate(VaultSession? session, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (session is null || !session.IsUnlocked)
        {
            return AutofillResponse.Locked();
        }

        if (request.EntryId is not { } entryId || request.AccountId is not { } accountId)
        {
            return AutofillResponse.Error("未选择账号。");
        }

        if (!session.VerifyShortKey(request.ShortKey))
        {
            return AutofillResponse.Error("短密钥不正确。");
        }

        var entry = session.Entries.FirstOrDefault(item => item.Id == entryId);
        var account = entry?.Accounts.FirstOrDefault(item => item.Id == accountId);
        if (entry is null || account is null || !AutofillMatcher.IsEligible(entry, account, request.Origin)
            || AutofillMatcher.Match([entry], request).All(match => match.AccountId != account.Id))
        {
            return AutofillResponse.Cancelled();
        }

        return AutofillResponse.Fill(account.Username, account.Password);
    }

    public static async Task<AutofillResponse> HandleDiscoverAsync(
        VaultSession? session,
        AutofillRequest request,
        IAutofillPrompter prompter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prompter);
        if (session is null || !session.IsUnlocked)
        {
            return AutofillResponse.Locked();
        }

        var matches = AutofillMatcher.Match(session.Entries, request);
        if (matches.Count == 0)
        {
            return AutofillResponse.NoMatch();
        }

        var decision = await prompter.PromptAsync(request, matches, session.VerifyShortKey, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            return AutofillResponse.Cancelled();
        }

        if (!session.IsUnlocked || !session.VerifyShortKey(decision.ShortKey))
        {
            return AutofillResponse.Error("短密钥不正确。");
        }

        var entry = session.Entries.FirstOrDefault(item => item.Id == decision.EntryId);
        var account = entry?.Accounts.FirstOrDefault(item => item.Id == decision.AccountId);
        if (entry is null || account is null || !AutofillMatcher.IsEligible(entry, account, request.Origin)
            || matches.All(match => match.Id != entry.Id || match.AccountId != account.Id)
            || AutofillMatcher.Match([entry], request).All(match => match.AccountId != account.Id))
        {
            return AutofillResponse.Cancelled();
        }

        return AutofillResponse.Fill(account.Username, account.Password);
    }
}
