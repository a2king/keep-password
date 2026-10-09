using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialFiller : ICredentialFiller
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryFill(DetectedPasswordField field, string username, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            IUIAutomationElement? passwordElement = null;
            IUIAutomationElement? usernameElement = null;
            var releasePassword = false;
            var releaseUsername = false;
            if (!WindowsPasswordFieldDetector.TryTakeCached(field.FieldKey, out passwordElement, out usernameElement))
            {
                var automation = (IUIAutomation)new CUIAutomation();
                try
                {
                    if (field.PasswordHandle != 0 && automation.ElementFromHandle(field.PasswordHandle, out passwordElement) >= 0)
                    {
                        releasePassword = true;
                    }

                    if (field.UsernameHandle != 0 && automation.ElementFromHandle(field.UsernameHandle, out usernameElement) >= 0)
                    {
                        releaseUsername = true;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(automation);
                }
            }

            var filled = false;
            if (usernameElement is not null && username.Length > 0)
            {
                filled |= SetText(usernameElement, username);
            }

            if (passwordElement is not null && password.Length > 0)
            {
                filled |= SetText(passwordElement, password);
            }

            if (releasePassword)
            {
                Marshal.ReleaseComObject(passwordElement!);
            }

            if (releaseUsername && usernameElement is not null)
            {
                Marshal.ReleaseComObject(usernameElement);
            }

            return filled;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SetText(IUIAutomationElement element, string text)
    {
        if (element.GetCurrentPattern(UiaIds.ValuePattern, out var patternObject) >= 0
            && patternObject is IUIAutomationValuePattern pattern)
        {
            var wrote = pattern.SetValue(text) >= 0;
            Marshal.ReleaseComObject(pattern);
            if (wrote)
            {
                return true;
            }
        }

        if (element.SetFocus() < 0)
        {
            return false;
        }

        return WindowsTextInput.TypeReplacing(text);
    }
}
