using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepPassword.App.Platform.Windows;

/// <summary>
/// 把整个虚拟桌面（所有显示器）抓成内存中的 BGRA 像素，只用于本地二维码识别，不落盘。
/// 调用方用完后必须清零返回的数组。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;

    public static (byte[] Pixels, int Width, int Height)? Capture()
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screen = GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32
        };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        var length = (nint)width * height * 4;
        try
        {
            if (memory == 0 || bitmap == 0 || bits == 0)
            {
                return null;
            }

            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, height, screen, left, top, SrcCopy | CaptureBlt);
            SelectObject(memory, previous);
            if (!copied)
            {
                return null;
            }

            var pixels = new byte[length];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return (pixels, width, height);
        }
        finally
        {
            if (bits != 0)
            {
                RtlZeroMemory(bits, length);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                DeleteDC(memory);
            }

            ReleaseDC(0, screen);
        }
    }

    /// <summary>Windows 10 2004 及以上可让窗口在截图里“隐身”；旧系统返回 false。</summary>
    public static bool ExcludeFromCapture(nint hwnd, bool exclude) =>
        hwnd != 0 && SetWindowDisplayAffinity(hwnd, exclude ? WdaExcludeFromCapture : WdaNone);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("kernel32.dll")]
    private static extern void RtlZeroMemory(nint destination, nint length);
}
