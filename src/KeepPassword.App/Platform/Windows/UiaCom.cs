using System.Runtime.InteropServices;

namespace KeepPassword.App.Platform.Windows;

internal static class UiaIds
{
    public const int Name = 30005;
    public const int ProcessId = 30002;
    public const int ControlType = 30003;
    public const int IsPassword = 30019;
    public const int Edit = 50004;
    public const int Window = 50032;
    public const int ValuePattern = 10002;
    public const int TreeScopeDescendants = 4;
}

[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation
{
}

[ComImport]
[Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    [PreserveSig]
    int get_Length(out int length);

    [PreserveSig]
    int GetElement(int index, out IUIAutomationElement element);
}

[ComImport]
[Guid("4042c624-389c-4afc-a630-9df854a541fc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    [PreserveSig]
    int GetParentElement(IUIAutomationElement element, out IUIAutomationElement parent);
}

[ComImport]
[Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationValuePattern
{
    [PreserveSig]
    int SetValue([MarshalAs(UnmanagedType.BStr)] string value);
}

/// <summary>
/// 方法顺序与 Windows SDK 的 IUIAutomationElement 一致，不能重排。
/// 未调用的槽位只用占位签名。
/// </summary>
[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    [PreserveSig] int SetFocus();
    [PreserveSig] int Reserved1();
    [PreserveSig] int Reserved2();
    [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray found);
    [PreserveSig] int Reserved4();
    [PreserveSig] int Reserved5();
    [PreserveSig] int Reserved6();
    [PreserveSig] int Reserved7();
    [PreserveSig] int Reserved8();
    [PreserveSig] int Reserved9();
    [PreserveSig] int Reserved10();
    [PreserveSig] int Reserved11();
    [PreserveSig] int Reserved12();
    [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object pattern);
    [PreserveSig] int Reserved14();
    [PreserveSig] int Reserved15();
    [PreserveSig] int Reserved16();
    [PreserveSig] int get_CurrentProcessId(out int processId);
    [PreserveSig] int get_CurrentControlType(out int controlType);
    [PreserveSig] int Reserved19();
    [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] int Reserved21();
    [PreserveSig] int Reserved22();
    [PreserveSig] int Reserved23();
    [PreserveSig] int Reserved24();
    [PreserveSig] int Reserved25();
    [PreserveSig] int Reserved26();
    [PreserveSig] int Reserved27();
    [PreserveSig] int Reserved28();
    [PreserveSig] int Reserved29();
    [PreserveSig] int Reserved30();
    [PreserveSig] int Reserved31();
    [PreserveSig] int get_CurrentIsPassword(out int isPassword);
    [PreserveSig] int get_CurrentNativeWindowHandle(out nint handle);
}

/// <summary>
/// 方法顺序与 Windows SDK 的 IUIAutomation 一致，不能重排。
/// </summary>
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig] int CompareElements(IUIAutomationElement first, IUIAutomationElement second, out int areSame);
    [PreserveSig] int Reserved1();
    [PreserveSig] int Reserved2();
    [PreserveSig] int ElementFromHandle(nint hwnd, out IUIAutomationElement element);
    [PreserveSig] int Reserved4();
    [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
    [PreserveSig] int Reserved6();
    [PreserveSig] int Reserved7();
    [PreserveSig] int Reserved8();
    [PreserveSig] int Reserved9();
    [PreserveSig] int Reserved10();
    [PreserveSig] int get_ControlViewWalker(out IUIAutomationTreeWalker walker);
    [PreserveSig] int Reserved12();
    [PreserveSig] int Reserved13();
    [PreserveSig] int Reserved14();
    [PreserveSig] int Reserved15();
    [PreserveSig] int Reserved16();
    [PreserveSig] int Reserved17();
    [PreserveSig] int Reserved18();
    [PreserveSig] int Reserved19();
    [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
    [PreserveSig] int Reserved21();
    [PreserveSig] int CreateAndCondition(IUIAutomationCondition first, IUIAutomationCondition second, out IUIAutomationCondition condition);
}
