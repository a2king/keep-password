using KeepPassword.Core.Messaging;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

public static class AutofillCoordinator
{
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
        if (entry is null)
        {
            return AutofillResponse.Cancelled();
        }

        return AutofillResponse.Fill(entry.Username, entry.Password);
    }
}
