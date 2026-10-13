using System.Runtime.Versioning;
using System.Security.Cryptography;
using Avalonia.Controls;
using KeepPassword.App.Platform.Windows;
using KeepPassword.Core.Totp;

namespace KeepPassword.App.Services;

public sealed record ScannedTotp(string Uri, TotpSettings Settings)
{
    /// <summary>默认参数只存 Base32，与旧版本和其它工具兼容；否则保留整条链接以带上位数、周期和算法。</summary>
    public string StoredSecret => Settings.IsDefault ? Settings.Secret : Uri;
}

public sealed record QrScanResult(IReadOnlyList<ScannedTotp> Codes, int IgnoredCount, bool HasMigration);

public static class QrScreenScanner
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>识别当前所有显示器上的 otpauth://totp 二维码；扫描期间本窗口不参与截图。</summary>
    public static async Task<QrScanResult> ScanAsync(Window owner)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new QrScanResult([], 0, false);
        }

        var hwnd = owner.TryGetPlatformHandle()?.Handle ?? 0;
        var excluded = WindowsScreenCapture.ExcludeFromCapture(hwnd, true);
        var state = owner.WindowState;
        try
        {
            if (!excluded)
            {
                owner.WindowState = WindowState.Minimized;
            }

            await Task.Delay(excluded ? 150 : 400);
            var texts = await Task.Run(CaptureAndDecode);

            var found = new List<ScannedTotp>();
            var ignored = 0;
            var migration = false;
            foreach (var text in texts)
            {
                if (OtpAuthUri.TryParse(text, out var settings))
                {
                    if (found.All(item => item.Settings.Secret != settings.Secret))
                    {
                        found.Add(new ScannedTotp(text.Trim(), settings));
                    }
                }
                else
                {
                    ignored++;
                    migration |= text.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase);
                }
            }

            return new QrScanResult(found, ignored, migration);
        }
        finally
        {
            if (excluded)
            {
                WindowsScreenCapture.ExcludeFromCapture(hwnd, false);
            }
            else
            {
                owner.WindowState = state;
                owner.Activate();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> CaptureAndDecode()
    {
        if (WindowsScreenCapture.Capture() is not { } screen)
        {
            return [];
        }

        try
        {
            return QrCodeScanner.Decode(screen.Pixels, screen.Width, screen.Height);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(screen.Pixels);
        }
    }
}
