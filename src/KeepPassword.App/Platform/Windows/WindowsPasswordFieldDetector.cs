using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPasswordFieldDetector : IPasswordFieldDetector
{
    private static IUIAutomation? _automation;
    private static IUIAutomationElement? _cachedPassword;
    private static IUIAutomationElement? _cachedUsername;
    private static string? _cachedKey;

    public bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<DetectedPasswordField> DetectForeground()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var field = Detect();
            return field is null ? [] : [field];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return [];
        }
    }

    internal static bool TryTakeCached(string fieldKey, out IUIAutomationElement? password, out IUIAutomationElement? username)
    {
        if (_cachedKey == fieldKey && _cachedPassword is not null)
        {
            password = _cachedPassword;
            username = _cachedUsername;
            return true;
        }

        password = null;
        username = null;
        return false;
    }

    private static DetectedPasswordField? Detect()
    {
        var automation = Automation();
        if (automation.GetFocusedElement(out var focused) < 0 || focused is null)
        {
            return null;
        }

        if (focused.get_CurrentIsPassword(out var isPassword) < 0 || isPassword == 0)
        {
            Release(focused);
            return null;
        }

        var window = FindWindow(automation, focused) ?? focused;
        if (window.get_CurrentProcessId(out var processId) >= 0 && processId == Environment.ProcessId)
        {
            Release(focused);
            if (!ReferenceEquals(window, focused))
            {
                Release(window);
            }

            return null;
        }

        window.get_CurrentName(out var title);
        focused.get_CurrentNativeWindowHandle(out var passwordHandle);
        WindowsProcessInfo.Query(processId, out var processName, out var elevated);
        if (AutofillPolicy.IsExtensionBrowser(processName))
        {
            Release(focused);
            if (!ReferenceEquals(window, focused))
            {
                Release(window);
            }

            return null;
        }

        var username = FindUsername(automation, window, focused, out var usernameHandle);
        var fieldKey = passwordHandle != 0
            ? processId + ":" + passwordHandle
            : processId + ":" + (title ?? "");

        Release(ref _cachedPassword);
        Release(ref _cachedUsername);
        _cachedPassword = focused;
        _cachedUsername = username;
        _cachedKey = fieldKey;
        if (!ReferenceEquals(window, focused))
        {
            Release(window);
        }

        return new DetectedPasswordField
        {
            FieldKey = fieldKey,
            WindowTitle = title ?? "",
            ProcessId = processId,
            PasswordHandle = passwordHandle,
            UsernameHandle = usernameHandle,
            ProcessName = processName,
            IsElevated = elevated
        };
    }

    private static IUIAutomationElement? FindWindow(IUIAutomation automation, IUIAutomationElement focused)
    {
        if (automation.get_ControlViewWalker(out var walker) < 0 || walker is null)
        {
            return null;
        }

        IUIAutomationElement? window = null;
        var current = focused;
        var owned = false;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                if (current.get_CurrentControlType(out var controlType) >= 0 && controlType == UiaIds.Window)
                {
                    window = current;
                }

                if (walker.GetParentElement(current, out var parent) < 0 || parent is null)
                {
                    break;
                }

                if (owned && !ReferenceEquals(current, window))
                {
                    Release(current);
                }

                current = parent;
                owned = true;
            }
        }
        finally
        {
            Release(walker);
        }

        return window;
    }

    private static IUIAutomationElement? FindUsername(
        IUIAutomation automation,
        IUIAutomationElement window,
        IUIAutomationElement password,
        out nint usernameHandle)
    {
        usernameHandle = 0;
        if (automation.CreatePropertyCondition(UiaIds.ControlType, UiaIds.Edit, out var condition) < 0 || condition is null)
        {
            return null;
        }

        try
        {
            if (window.FindAll(UiaIds.TreeScopeDescendants, condition, out var elements) < 0 || elements is null)
            {
                return null;
            }

            try
            {
                if (elements.get_Length(out var length) < 0)
                {
                    return null;
                }

                var edits = new List<IUIAutomationElement>(length);
                var passwordIndex = -1;
                for (var i = 0; i < length; i++)
                {
                    if (elements.GetElement(i, out var edit) < 0 || edit is null)
                    {
                        continue;
                    }

                    edits.Add(edit);
                    if (automation.CompareElements(edit, password, out var same) >= 0 && same != 0)
                    {
                        passwordIndex = edits.Count - 1;
                    }
                }

                IUIAutomationElement? username = null;
                for (var i = passwordIndex - 1; i >= 0; i--)
                {
                    if (edits[i].get_CurrentIsPassword(out var masked) >= 0 && masked == 0)
                    {
                        username = edits[i];
                        username.get_CurrentNativeWindowHandle(out usernameHandle);
                        break;
                    }
                }

                foreach (var edit in edits)
                {
                    if (!ReferenceEquals(edit, username))
                    {
                        Release(edit);
                    }
                }

                return username;
            }
            finally
            {
                Release(elements);
            }
        }
        finally
        {
            Release(condition);
        }
    }

    private static IUIAutomation Automation()
    {
        _automation ??= (IUIAutomation)new CUIAutomation();
        return _automation;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private static void Release(ref IUIAutomationElement? com)
    {
        Release(com);
        com = null;
    }
}
