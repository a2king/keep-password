using System.Runtime.InteropServices;
using System.Text;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// macOS Accessibility (AXUIElement) 封装：权限检查、焦点密码框识别、写入字段值。
/// 调用方须先用 OperatingSystem.IsMacOS() 守卫。
/// </summary>
internal static class MacOsAccessibility
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly nint KAxFocusedUiElement = CreateConstant("AXFocusedUIElement");
    private static readonly nint KAxRole = CreateConstant("AXRole");
    private static readonly nint KAxSubrole = CreateConstant("AXSubrole");
    private static readonly nint KAxValue = CreateConstant("AXValue");
    private static readonly nint KAxTitle = CreateConstant("AXTitle");
    private static readonly nint KAxDescription = CreateConstant("AXDescription");
    private static readonly nint KAxIdentifier = CreateConstant("AXIdentifier");
    private static readonly nint KAxParent = CreateConstant("AXParent");
    private static readonly nint KAxChildren = CreateConstant("AXChildren");
    private static readonly nint KAxPosition = CreateConstant("AXPosition");
    private static readonly nint KAxSize = CreateConstant("AXSize");
    private static readonly nint KAxTrustedCheckOptionPrompt = CreateConstant("AXTrustedCheckOptionPrompt");

    private const int AxValueCgPointType = 1;
    private const int AxValueCgSizeType = 2;

    public static bool IsTrusted(bool prompt)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!prompt)
        {
            return AXIsProcessTrusted();
        }

        var options = CFDictionaryCreate(
            0,
            [KAxTrustedCheckOptionPrompt],
            [KCfBooleanTrue],
            1,
            nint.Zero,
            nint.Zero);
        try
        {
            return AXIsProcessTrustedWithOptions(options);
        }
        finally
        {
            if (options != 0)
            {
                CFRelease(options);
            }
        }
    }

    public static bool TryDetectFocusedPassword(
        out nint passwordElement,
        out nint usernameElement,
        out string windowTitle,
        out int processId,
        out string processName,
        out string stableFieldKey)
    {
        passwordElement = 0;
        usernameElement = 0;
        windowTitle = "";
        processId = 0;
        processName = "";
        stableFieldKey = "";

        if (!IsTrusted(prompt: false))
        {
            return false;
        }

        var systemWide = AXUIElementCreateSystemWide();
        if (systemWide == 0)
        {
            return false;
        }

        try
        {
            if (AXUIElementCopyAttributeValue(systemWide, KAxFocusedUiElement, out var focused) != 0 || focused == 0)
            {
                return false;
            }

            try
            {
                if (!IsPasswordField(focused))
                {
                    return false;
                }

                if (AXUIElementGetPid(focused, out processId) != 0 || processId == Environment.ProcessId)
                {
                    return false;
                }

                processName = ResolveProcessName(processId);
                windowTitle = FindWindowTitle(focused);
                var username = FindUsernameNear(focused);
                stableFieldKey = BuildStableFieldKey(focused, processId, windowTitle);
                passwordElement = Retain(focused);
                usernameElement = username;
                return passwordElement != 0;
            }
            finally
            {
                CFRelease(focused);
            }
        }
        finally
        {
            CFRelease(systemWide);
        }
    }

    private static string BuildStableFieldKey(nint element, int processId, string windowTitle)
    {
        TryGetPoint(element, KAxPosition, out var x, out var y);
        TryGetSize(element, KAxSize, out var w, out var h);
        var label = FieldLabel(element);
        // 位置取整，避免亚像素抖动导致 key 变化而反复弹窗。
        return string.Join(':',
            processId,
            windowTitle,
            label,
            ((int)Math.Round(x)).ToString(),
            ((int)Math.Round(y)).ToString(),
            ((int)Math.Round(w)).ToString(),
            ((int)Math.Round(h)).ToString());
    }

    private static bool TryGetPoint(nint element, nint attribute, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (AXUIElementCopyAttributeValue(element, attribute, out var value) != 0 || value == 0)
        {
            return false;
        }

        try
        {
            if (!AXValueGetValue(value, AxValueCgPointType, out CgPoint point))
            {
                return false;
            }

            x = point.X;
            y = point.Y;
            return true;
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static bool TryGetSize(nint element, nint attribute, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (AXUIElementCopyAttributeValue(element, attribute, out var value) != 0 || value == 0)
        {
            return false;
        }

        try
        {
            if (!AXValueGetValue(value, AxValueCgSizeType, out CgSize size))
            {
                return false;
            }

            width = size.Width;
            height = size.Height;
            return true;
        }
        finally
        {
            CFRelease(value);
        }
    }

    public static bool TrySetValue(nint element, string value)
    {
        if (element == 0)
        {
            return false;
        }

        var cf = CreateCfString(value);
        if (cf == 0)
        {
            return false;
        }

        try
        {
            return AXUIElementSetAttributeValue(element, KAxValue, cf) == 0;
        }
        finally
        {
            CFRelease(cf);
        }
    }

    public static nint Retain(nint element)
    {
        if (element != 0)
        {
            CFRetain(element);
        }

        return element;
    }

    public static void Release(ref nint element)
    {
        if (element != 0)
        {
            CFRelease(element);
            element = 0;
        }
    }

    private static bool IsPasswordField(nint element)
    {
        var role = CopyStringAttribute(element, KAxRole);
        if (string.Equals(role, "AXSecureTextField", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(role, "AXTextField", StringComparison.Ordinal))
        {
            return false;
        }

        var subrole = CopyStringAttribute(element, KAxSubrole);
        return string.Equals(subrole, "AXSecureTextField", StringComparison.Ordinal);
    }

    private static bool IsEditableTextField(nint element)
    {
        if (IsPasswordField(element))
        {
            return false;
        }

        var role = CopyStringAttribute(element, KAxRole);
        return string.Equals(role, "AXTextField", StringComparison.Ordinal)
            || string.Equals(role, "AXTextArea", StringComparison.Ordinal)
            || string.Equals(role, "AXComboBox", StringComparison.Ordinal);
    }

    private static string FindWindowTitle(nint element)
    {
        var current = Retain(element);
        try
        {
            for (var i = 0; i < 40 && current != 0; i++)
            {
                var role = CopyStringAttribute(current, KAxRole);
                if (string.Equals(role, "AXWindow", StringComparison.Ordinal)
                    || string.Equals(role, "AXSheet", StringComparison.Ordinal)
                    || string.Equals(role, "AXDialog", StringComparison.Ordinal))
                {
                    var title = CopyStringAttribute(current, KAxTitle);
                    if (title.Length > 0)
                    {
                        return title;
                    }
                }

                if (AXUIElementCopyAttributeValue(current, KAxParent, out var parent) != 0 || parent == 0)
                {
                    break;
                }

                Release(ref current);
                current = parent;
            }
        }
        finally
        {
            Release(ref current);
        }

        return "";
    }

    private static nint FindUsernameNear(nint password)
    {
        // 在同一父节点的子控件里，取密码框前面最近的、标签像用户名的可编辑文本框。
        if (AXUIElementCopyAttributeValue(password, KAxParent, out var parent) != 0 || parent == 0)
        {
            return 0;
        }

        try
        {
            if (AXUIElementCopyAttributeValue(parent, KAxChildren, out var children) != 0 || children == 0)
            {
                return 0;
            }

            try
            {
                var count = (int)CFArrayGetCount(children);
                var passwordIndex = -1;
                var childrenList = new nint[count];
                for (var i = 0; i < count; i++)
                {
                    childrenList[i] = CFArrayGetValueAtIndex(children, i);
                    if (childrenList[i] != 0 && SameElement(childrenList[i], password))
                    {
                        passwordIndex = i;
                    }
                }

                if (passwordIndex < 0)
                {
                    return 0;
                }

                for (var i = passwordIndex - 1; i >= 0; i--)
                {
                    var child = childrenList[i];
                    if (child == 0)
                    {
                        continue;
                    }

                    if (IsPasswordField(child))
                    {
                        break;
                    }

                    if (!IsEditableTextField(child))
                    {
                        continue;
                    }

                    if (IsUsernameLabel(FieldLabel(child)))
                    {
                        return Retain(child);
                    }
                }

                return 0;
            }
            finally
            {
                CFRelease(children);
            }
        }
        finally
        {
            CFRelease(parent);
        }
    }

    private static bool SameElement(nint left, nint right)
    {
        // AXUIElement 指针相等即可；部分系统上 CFEqual 也可。
        if (left == right)
        {
            return true;
        }

        return CFEqual(left, right);
    }

    private static string FieldLabel(nint element)
    {
        foreach (var attribute in new[] { KAxTitle, KAxDescription, KAxIdentifier })
        {
            var value = CopyStringAttribute(element, attribute);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    private static bool IsUsernameLabel(string? name)
    {
        var label = (name ?? "").Trim().TrimEnd(':', '：', '*').Trim().ToLowerInvariant();
        return label is "username" or "user name" or "userid" or "user id" or "user"
            or "account" or "account name" or "login" or "login name"
            or "email" or "e-mail" or "email address" or "e-mail address"
            or "phone" or "phone number" or "mobile" or "mobile number"
            or "用户名" or "用户名称" or "用户" or "账号" or "帐号" or "账户" or "账户名" or "帐户"
            or "登录名" or "邮箱" or "邮箱地址" or "电子邮箱" or "手机号" or "手机号码";
    }

    private static string CopyStringAttribute(nint element, nint attribute)
    {
        if (AXUIElementCopyAttributeValue(element, attribute, out var value) != 0 || value == 0)
        {
            return "";
        }

        try
        {
            return CfStringToManaged(value);
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static string CfStringToManaged(nint cfString)
    {
        if (cfString == 0)
        {
            return "";
        }

        var length = (int)CFStringGetLength(cfString);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length];
        var range = new CfRange { Location = 0, Length = length };
        CFStringGetCharacters(cfString, range, buffer);
        return new string(buffer);
    }

    private static nint CreateCfString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return CFStringCreateWithBytes(0, bytes, bytes.Length, 0x08000100 /* kCFStringEncodingUTF8 */, false);
    }

    private static nint CreateConstant(string name)
    {
        // 延迟到首次真正调用 Mac API 时再创建；构造时若在非 Mac 上跑到会失败。
        // 这里用静态字段初始化，仅在 macOS 加载时安全。
        if (!OperatingSystem.IsMacOS())
        {
            return 0;
        }

        return CreateCfString(name);
    }

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    // CFBoolean true 是全局常量符号。
    private static readonly nint KCfBooleanTrue = GetCfBooleanTrue();

    private static nint GetCfBooleanTrue()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return 0;
        }

        return dlsym(dlopen(CoreFoundation, 0), "kCFBooleanTrue");
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint dlsym(nint handle, string symbol);

    [StructLayout(LayoutKind.Sequential)]
    private struct CfRange
    {
        public long Location;
        public long Length;
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXIsProcessTrustedWithOptions(nint options);

    [DllImport(ApplicationServices)]
    private static extern nint AXUIElementCreateSystemWide();

    [DllImport(ApplicationServices)]
    private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);

    [DllImport(ApplicationServices)]
    private static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);

    [DllImport(ApplicationServices)]
    private static extern int AXUIElementGetPid(nint element, out int pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct CgPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CgSize
    {
        public double Width;
        public double Height;
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXValueGetValue(nint value, int type, out CgPoint pointValue);

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXValueGetValue(nint value, int type, out CgSize sizeValue);

    [DllImport(CoreFoundation)]
    private static extern nint CFRetain(nint cf);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFEqual(nint cf1, nint cf2);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetLength(nint theString);

    [DllImport(CoreFoundation)]
    private static extern void CFStringGetCharacters(nint theString, CfRange range, [Out] char[] buffer);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithBytes(
        nint allocator, byte[] bytes, long numBytes, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [DllImport(CoreFoundation)]
    private static extern long CFArrayGetCount(nint theArray);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint theArray, long index);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreate(
        nint allocator,
        nint[] keys,
        nint[] values,
        long numValues,
        nint keyCallBacks,
        nint valueCallBacks);
}
