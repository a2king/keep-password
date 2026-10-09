using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Linux;

/// <summary>
/// Linux AT-SPI 补全的空实现。接口已留出，后续再接入。
/// </summary>
public sealed class LinuxPasswordFieldDetector : IPasswordFieldDetector
{
    public bool IsSupported => false;

    public IReadOnlyList<DetectedPasswordField> DetectForeground() => [];
}

public sealed class LinuxCredentialFiller : ICredentialFiller
{
    public bool IsSupported => false;

    public bool TryFill(DetectedPasswordField field, string username, string password) => false;
}
