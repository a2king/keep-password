using System.Security.Cryptography;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;
using ZXing.QrCode;

namespace KeepPassword.Core.Totp;

public static class QrCodeScanner
{
    private static readonly Dictionary<DecodeHintType, object> Hints = new()
    {
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
    };

    /// <summary>识别 32 位 BGRA 像素（自上而下、无行填充）中的全部二维码文本。</summary>
    public static IReadOnlyList<string> Decode(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException("像素数据与尺寸不匹配。", nameof(bgra));
        }

        var luminance = new byte[width * height];
        try
        {
            for (int i = 0, p = 0; i < luminance.Length; i++, p += 4)
            {
                luminance[i] = (byte)((bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8);
            }

            var bitmap = new BinaryBitmap(new HybridBinarizer(new GrayLuminanceSource(luminance, width, height)));
            var texts = new List<string>();
            var results = new QRCodeMultiReader().decodeMultiple(bitmap, Hints);
            if (results is { Length: > 0 })
            {
                texts.AddRange(results.Select(result => result.Text));
            }
            else if (new QRCodeReader().decode(bitmap, Hints) is { } single)
            {
                texts.Add(single.Text);
            }

            return texts.Where(text => !string.IsNullOrEmpty(text)).Distinct(StringComparer.Ordinal).ToList();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(luminance);
        }
    }

    private sealed class GrayLuminanceSource(byte[] pixels, int width, int height) : LuminanceSource(width, height)
    {
        public override byte[] Matrix => pixels;

        public override byte[] getRow(int y, byte[] row)
        {
            if (row is null || row.Length < Width)
            {
                row = new byte[Width];
            }

            Array.Copy(pixels, y * Width, row, 0, Width);
            return row;
        }
    }
}
