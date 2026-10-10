namespace KeepPassword.Core.Autofill;

public enum AutofillOrigin
{
    Browser,
    WindowsApplication,
    MacOsApplication,
    LinuxApplication
}

public sealed class AutofillRequest
{
    public AutofillOrigin Origin { get; init; }

    public string? Url { get; init; }

    public string? WindowTitle { get; init; }

    public string? TargetProcess { get; init; }

    public string? ClientToken { get; init; }
}

public sealed class AutofillCandidate
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Username { get; init; }

    public required string Domain { get; init; }
}

public sealed class DetectedPasswordField
{
    public required string FieldKey { get; init; }

    public string WindowTitle { get; init; } = "";

    public int ProcessId { get; init; }

    public nint PasswordHandle { get; init; }

    public nint UsernameHandle { get; init; }

    public string ProcessName { get; init; } = "";

    public bool IsElevated { get; init; }
}

public sealed class AutofillDecision
{
    public required Guid EntryId { get; init; }

    public required string ShortKey { get; init; }
}

public interface IAutofillPrompter
{
    Task<AutofillDecision?> PromptAsync(
        AutofillRequest request,
        IReadOnlyList<AutofillCandidate> matches,
        Func<string, bool> verifyShortKey,
        CancellationToken cancellationToken);
}

public interface IPasswordFieldDetector
{
    bool IsSupported { get; }

    IReadOnlyList<DetectedPasswordField> DetectForeground();
}

public interface ICredentialFiller
{
    bool IsSupported { get; }

    bool TryFill(DetectedPasswordField field, string username, string password);
}
