using Avalonia.Input.Platform;

namespace KeepPassword.App.Services;

public static class SecretClipboard
{
    public static async Task CopyAsync(IClipboard clipboard, string value)
    {
        _ = await ClipboardService.CopyAsync(clipboard, value, sensitive: true);
    }
}
