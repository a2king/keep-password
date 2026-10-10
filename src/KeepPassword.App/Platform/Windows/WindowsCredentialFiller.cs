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

            var filled = Type(usernameElement, passwordElement, username, password);
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

    private static bool Type(IUIAutomationElement? usernameElement, IUIAutomationElement? passwordElement, string username, string password)
    {
        var typed = false;
        foreach (var step in AutofillPolicy.PlanAutoType(username, password))
        {
            if (step.Action == AutoTypeAction.Tab)
            {
                typed |= WindowsTextInput.TypeTab();
                continue;
            }

            var typingUsername = username.Length > 0 && step.Text == username && usernameElement is not null;
            var target = typingUsername ? usernameElement : passwordElement;
            if (target is null)
            {
                continue;
            }

            if (target.SetFocus() < 0)
            {
                return false;
            }

            Thread.Sleep(30);
            typed |= WindowsTextInput.TypeReplacing(step.Text ?? "");
        }

        return typed;
    }
}
