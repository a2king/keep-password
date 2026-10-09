using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// macOS 辅助功能补全的空实现。接口已留出，后续再接入。
/// </summary>
public sealed class MacOsPasswordFieldDetector : IPasswordFieldDetector
{
    public bool IsSupported => false;

    public IReadOnlyList<DetectedPasswordField> DetectForeground() => [];
}

public sealed class MacOsCredentialFiller : ICredentialFiller
{
    public bool IsSupported => false;

    public bool TryFill(DetectedPasswordField field, string username, string password) => false;
}
