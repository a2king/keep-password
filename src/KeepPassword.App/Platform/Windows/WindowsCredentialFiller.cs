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

        WindowsProcessInfo.Query(field.ProcessId, out _, out var elevatedNow);
        if (!AutofillPolicy.AllowAutoType(field.IsElevated || elevatedNow))
        {
            return false;
        }

        try
        {
            IUIAutomationElement? passwordElement = null;
            IUIAutomationElement? usernameElement = null;
            var releasePassword = false;
            var releaseUsername = false;
            var automation = (IUIAutomation)new CUIAutomation();
            try
            {
                if (!WindowsPasswordFieldDetector.TryTakeCached(field.FieldKey, out passwordElement, out usernameElement))
                {
                    if (field.PasswordHandle != 0 && automation.ElementFromHandle(field.PasswordHandle, out passwordElement) >= 0)
                    {
                        releasePassword = passwordElement is not null;
                    }

                    if (field.UsernameHandle != 0 && automation.ElementFromHandle(field.UsernameHandle, out usernameElement) >= 0)
                    {
                        releaseUsername = usernameElement is not null;
                    }
                }

                return Type(automation, field.ProcessId, usernameElement, passwordElement, username, password);
            }
            finally
            {
                if (releasePassword && passwordElement is not null)
                {
                    Marshal.ReleaseComObject(passwordElement);
                }

                if (releaseUsername && usernameElement is not null)
                {
                    Marshal.ReleaseComObject(usernameElement);
                }

                Marshal.ReleaseComObject(automation);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Type(
        IUIAutomation automation,
        int processId,
        IUIAutomationElement? usernameElement,
        IUIAutomationElement? passwordElement,
        string username,
        string password)
    {
        if (!IsTarget(passwordElement, processId, isPassword: true))
        {
            return false;
        }

        // Each value has an explicit target. Comparing the values misroutes the
        // password when it happens to be identical to the username.
        if (username.Length > 0 && usernameElement is not null)
        {
            if (!IsTarget(usernameElement, processId, isPassword: false)
                || !TypeInto(automation, usernameElement, username))
            {
                return false;
            }
        }

        // Password-only screens do not need a synthetic username or Tab key.
        return TypeInto(automation, passwordElement!, password);
    }

    private static bool IsTarget(IUIAutomationElement? target, int processId, bool isPassword) =>
        target is not null
        && target.get_CurrentProcessId(out var actualProcessId) >= 0
        && actualProcessId == processId
        && target.get_CurrentIsPassword(out var actualIsPassword) >= 0
        && (actualIsPassword != 0) == isPassword;

    private static bool TypeInto(IUIAutomation automation, IUIAutomationElement target, string text)
    {
        if (target.SetFocus() < 0)
        {
            return false;
        }

        Thread.Sleep(30);
        if (automation.GetFocusedElement(out var focused) < 0 || focused is null)
        {
            return false;
        }

        try
        {
            if (automation.CompareElements(target, focused, out var same) < 0 || same == 0)
            {
                return false;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(focused);
        }

        return WindowsTextInput.TypeReplacing(text);
    }
}
