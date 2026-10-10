namespace KeepPassword.Core.Autofill;

public enum AutoTypeAction
{
    Text,
    Tab
}

public readonly record struct AutoTypeStep(AutoTypeAction Action, string? Text);

public static class AutofillPolicy
{
    public static bool IsExtensionBrowser(string? processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName ?? "");
        return name.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msedge", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AllowAutoType(bool targetElevated) => !targetElevated;

    public static IReadOnlyList<AutoTypeStep> PlanAutoType(string? username, string? password)
    {
        var steps = new List<AutoTypeStep>();
        if (!string.IsNullOrEmpty(username))
        {
            steps.Add(new AutoTypeStep(AutoTypeAction.Text, username));
            steps.Add(new AutoTypeStep(AutoTypeAction.Tab, null));
        }

        steps.Add(new AutoTypeStep(AutoTypeAction.Text, password ?? ""));
        return steps;
    }
}

public static class SessionLockPolicy
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    public static bool ShouldLock(TimeSpan idle, bool workstationLocked, bool suspending) =>
        workstationLocked || suspending || idle >= IdleLimit;
}
