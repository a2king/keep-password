using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// 通过 macOS Accessibility 识别前台密码框（对齐 1Password Universal Autofill）。
/// </summary>
public sealed class MacOsPasswordFieldDetector : IPasswordFieldDetector
{
    private static nint _cachedPassword;
    private static nint _cachedUsername;
    private static string? _cachedKey;

    public bool IsSupported => OperatingSystem.IsMacOS();

    public IReadOnlyList<DetectedPasswordField> DetectForeground()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        try
        {
            if (!MacOsAccessibility.TryDetectFocusedPassword(
                    out var password,
                    out var username,
                    out var title,
                    out var processId,
                    out var processName,
                    out var fieldKey))
            {
                return [];
            }

            if (AutofillPolicy.IsExtensionBrowser(processName))
            {
                MacOsAccessibility.Release(ref password);
                MacOsAccessibility.Release(ref username);
                return [];
            }

            MacOsAccessibility.Release(ref _cachedPassword);
            MacOsAccessibility.Release(ref _cachedUsername);
            _cachedPassword = password;
            _cachedUsername = username;
            _cachedKey = fieldKey;

            return
            [
                new DetectedPasswordField
                {
                    FieldKey = fieldKey,
                    WindowTitle = title,
                    ProcessId = processId,
                    PasswordHandle = password,
                    UsernameHandle = username,
                    HasUsernameField = username != 0,
                    ProcessName = processName,
                    IsElevated = false
                }
            ];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    internal static bool TryTakeCached(string fieldKey, out nint password, out nint username)
    {
        if (_cachedKey == fieldKey && _cachedPassword != 0)
        {
            password = MacOsAccessibility.Retain(_cachedPassword);
            username = MacOsAccessibility.Retain(_cachedUsername);
            return true;
        }

        password = 0;
        username = 0;
        return false;
    }

    public static bool EnsureAccessibility(bool prompt) =>
        OperatingSystem.IsMacOS() && MacOsAccessibility.IsTrusted(prompt);
}

/// <summary>
/// 通过 Accessibility 写入用户名/密码；失败时降级为 CGEvent 键盘模拟。
/// </summary>
public sealed class MacOsCredentialFiller : ICredentialFiller
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    public bool TryFill(DetectedPasswordField field, string username, string password)
    {
        if (!OperatingSystem.IsMacOS() || !AutofillPolicy.AllowAutoType(field.IsElevated))
        {
            return false;
        }

        nint passwordElement = 0;
        nint usernameElement = 0;
        try
        {
            if (!MacOsPasswordFieldDetector.TryTakeCached(field.FieldKey, out passwordElement, out usernameElement))
            {
                passwordElement = MacOsAccessibility.Retain(field.PasswordHandle);
                usernameElement = MacOsAccessibility.Retain(field.UsernameHandle);
            }

            if (passwordElement == 0)
            {
                return false;
            }

            var filledPassword = MacOsAccessibility.TrySetValue(passwordElement, password);
            var filledUsername = true;
            if (field.HasUsernameField && usernameElement != 0 && !string.IsNullOrEmpty(username))
            {
                filledUsername = MacOsAccessibility.TrySetValue(usernameElement, username);
            }

            if (filledPassword && filledUsername)
            {
                return true;
            }

            return MacOsKeyboard.TryType(username, password, field.HasUsernameField);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            MacOsAccessibility.Release(ref passwordElement);
            MacOsAccessibility.Release(ref usernameElement);
        }
    }
}
