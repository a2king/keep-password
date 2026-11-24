using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;
using BarcodeFormat = ZXing.BarcodeFormat;
using EncodeHintType = ZXing.EncodeHintType;
using ZXing.QrCode;

namespace KeepPassword.Core.Sync;

/// <summary>
/// 设备配对的加密部分。服务器只中转本类产生的承诺、临时公钥和密文信封。
/// <list type="bullet">
/// <item>新设备先只提交公钥承诺，老设备给出公钥后新设备才公开自己的公钥；
/// 服务器冒充任一方都必须先选定密钥，无法挑出让两边 6 位确认码相同的密钥。</item>
/// <item>数据密钥用两边临时 X25519 密钥协商出的 AES-256-GCM 密钥加密。</item>
/// <item>扫码批准时，二维码里的随机秘密参与密钥派生。只有真正扫到二维码的设备能做出新设备解得开的信封，
/// 所以扫码模式不需要再核对确认码；确认码模式下新设备必须由用户确认两边一致。</item>
/// </list>
/// </summary>
public static class PairingCrypto
{
    public const string ModeCode = "code";
    public const string ModeQr = "qr";
    public const string QrPrefix = "kppair1:";
    private static readonly byte[] CommitPrefix = Encoding.ASCII.GetBytes("keep-password/pair-commit/v1");
    private static readonly byte[] CodeSalt = Encoding.ASCII.GetBytes("keep-password/pair-code/v1");
    private const string EnvelopeInfo = "keep-password/pair-envelope/v1";

    public sealed class KeyPair : IDisposable
    {
        internal KeyPair(byte[] privateKey, byte[] publicKey)
        {
            PrivateKey = privateKey;
            PublicKey = publicKey;
        }

        internal byte[] PrivateKey { get; }
        public byte[] PublicKey { get; }
        public string PublicKeyText => Convert.ToBase64String(PublicKey);
        public void Dispose() => CryptographicOperations.ZeroMemory(PrivateKey);
    }

    public sealed record OpenedKey(byte[] DataKey, bool Authenticated);

    public static KeyPair Generate()
    {
        var privateKey = new byte[X25519.ScalarSize];
        X25519.GeneratePrivateKey(new SecureRandom(), privateKey);
        var publicKey = new byte[X25519.PointSize];
        X25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        return new KeyPair(privateKey, publicKey);
    }

    public static string Commitment(byte[] publicKey) =>
        Convert.ToHexString(SHA256.HashData([.. CommitPrefix, .. publicKey])).ToLowerInvariant();

    public static bool MatchesCommitment(byte[] publicKey, string? commitment) =>
        commitment is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Commitment(publicKey)), Encoding.ASCII.GetBytes(commitment.Trim().ToLowerInvariant()));

    /// <summary>两边显示的 6 位确认码，例如 "482 913"。只取决于双方公钥。</summary>
    public static string ConfirmationCode(byte[] requesterPublicKey, byte[] approverPublicKey)
    {
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, [.. requesterPublicKey, .. approverPublicKey], 4, CodeSalt, []);
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes) % 1_000_000;
        var text = value.ToString("D6");
        return text[..3] + " " + text[3..];
    }

    public static byte[] ParsePublicKey(string? text)
    {
        try
        {
            var key = Convert.FromBase64String(text ?? "");
            if (key.Length == X25519.PointSize) return key;
        }
        catch (FormatException)
        {
        }

        throw new SyncApiException(SyncFailureKind.Validation, "配对数据无效，请重新发起。");
    }

    /// <summary>老设备：把数据密钥加密给新设备。qrSecret 不为空表示通过扫码批准。</summary>
    public static string Seal(KeyPair approver, byte[] requesterPublicKey, string pairingId, byte[] dataKey, byte[]? qrSecret = null)
    {
        var mode = qrSecret is null ? ModeCode : ModeQr;
        var key = EnvelopeKey(approver.PrivateKey, requesterPublicKey, requesterPublicKey, approver.PublicKey, pairingId, qrSecret, out var aad);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[dataKey.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
            {
                gcm.Encrypt(nonce, dataKey, cipher, tag, Aad(aad, mode));
            }

            return JsonSerializer.Serialize(new Envelope
            {
                V = 1, Mode = mode,
                Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher)
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>新设备：解开信封。扫码模式且解得开才算已认证；确认码模式需要用户核对两边一致。</summary>
    public static OpenedKey Open(KeyPair requester, byte[] approverPublicKey, string pairingId, string envelopeText, byte[] qrSecret)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(envelopeText);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is not { V: 1 } || envelope.Mode is not (ModeCode or ModeQr))
        {
            throw new SyncApiException(SyncFailureKind.Validation, "收到的密钥无法识别，请重新发起配对。");
        }

        var secret = envelope.Mode == ModeQr ? qrSecret : null;
        var key = EnvelopeKey(requester.PrivateKey, approverPublicKey, requester.PublicKey, approverPublicKey, pairingId, secret, out var aad);
        try
        {
            var nonce = Convert.FromBase64String(envelope.Nonce ?? "");
            var tag = Convert.FromBase64String(envelope.Tag ?? "");
            var cipher = Convert.FromBase64String(envelope.Data ?? "");
            if (cipher.Length != 32) throw new CryptographicException();
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, 16))
            {
                gcm.Decrypt(nonce, cipher, tag, plain, Aad(aad, envelope.Mode));
            }

            return new OpenedKey(plain, envelope.Mode == ModeQr);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new SyncApiException(SyncFailureKind.Validation, "收到的密钥无法解开，可能被篡改。已停止，本地数据未改动。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Aad(byte[] context, string mode) => [.. context, .. Encoding.ASCII.GetBytes("|" + mode)];

    private static byte[] EnvelopeKey(byte[] privateKey, byte[] peerPublicKey, byte[] requesterPublicKey, byte[] approverPublicKey,
        string pairingId, byte[]? qrSecret, out byte[] aad)
    {
        var shared = new byte[X25519.PointSize];
        try
        {
            if (!X25519.CalculateAgreement(privateKey, 0, peerPublicKey, 0, shared, 0))
            {
                throw new SyncApiException(SyncFailureKind.Validation, "配对数据无效，请重新发起。");
            }

            aad = Encoding.UTF8.GetBytes($"{EnvelopeInfo}|{pairingId}|{Convert.ToBase64String(requesterPublicKey)}|{Convert.ToBase64String(approverPublicKey)}");
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, qrSecret ?? [], aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>二维码内容：配对编号、新设备公钥和只在二维码里出现的随机秘密。不含任何数据密钥。</summary>
    public static string QrPayload(string pairingId, byte[] requesterPublicKey, byte[] qrSecret) =>
        QrPrefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(new QrBody
        {
            I = pairingId, K = Base64Url(requesterPublicKey), S = Base64Url(qrSecret)
        }));

    public static (string PairingId, byte[] RequesterPublicKey, byte[] QrSecret) ParseQr(string payload)
    {
        try
        {
            var text = (payload ?? "").Trim();
            if (!text.StartsWith(QrPrefix, StringComparison.Ordinal)) throw new FormatException();
            var body = JsonSerializer.Deserialize<QrBody>(FromBase64Url(text[QrPrefix.Length..])) ?? throw new FormatException();
            var key = FromBase64Url(body.K ?? "");
            var secret = FromBase64Url(body.S ?? "");
            if (string.IsNullOrWhiteSpace(body.I) || key.Length != X25519.PointSize || secret.Length < 16) throw new FormatException();
            return (body.I, key, secret);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new SyncApiException(SyncFailureKind.Validation, "这不是 Keep Password 的设备配对二维码。");
        }
    }

    /// <summary>生成二维码点阵（含 2 格留白），true 为深色。</summary>
    public static bool[,] QrMatrix(string payload)
    {
        var matrix = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 2, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        var cells = new bool[matrix.Width, matrix.Height];
        for (var x = 0; x < matrix.Width; x++)
        for (var y = 0; y < matrix.Height; y++)
            cells[x, y] = matrix[x, y];
        return cells;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private sealed class Envelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("v")] public int V { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("mode")] public string? Mode { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("nonce")] public string? Nonce { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tag")] public string? Tag { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("data")] public string? Data { get; set; }
    }

    private sealed class QrBody
    {
        [System.Text.Json.Serialization.JsonPropertyName("i")] public string? I { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("k")] public string? K { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("s")] public string? S { get; set; }
    }
}
