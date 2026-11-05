using System.Security.Cryptography;
using KeepPassword.Core.Update;

namespace KeepPassword.Release;

internal static class Commands
{
    public static int KeyGen(Options options)
    {
        var privatePath = Path.GetFullPath(options.Require("private"));
        if (File.Exists(privatePath))
        {
            throw new IOException($"{privatePath} 已存在，不会覆盖。换个文件名，或确认不再需要后手动删除。");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem;
        if (options.Has("no-password"))
        {
            Console.Error.WriteLine("警告：私钥将以明文保存，任何能读到这个文件的人都能伪造更新。");
            pem = key.ExportPkcs8PrivateKeyPem();
        }
        else
        {
            var password = Secrets.ReadPassword("给私钥设一个密码（至少 8 位）：", confirm: true);
            pem = key.ExportEncryptedPkcs8PrivateKeyPem(
                password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000));
        }

        var folder = Path.GetDirectoryName(privatePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(privatePath, pem);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var trustPath = TrustFile.Locate(options.Get("trust-file"));
        TrustFile.AddKey(trustPath, publicKey);

        Console.WriteLine($"私钥已保存：{privatePath}");
        Console.WriteLine("  请立刻备份到离线位置（密码管理器之外的 U 盘等）。丢了它就无法再发布能通过验签的更新。");
        Console.WriteLine($"公钥已写入：{trustPath}");
        Console.WriteLine("  下一步：提交这处改动并发布新版本，之后的程序才会要求更新必须带签名。");
        return 0;
    }

    public static int Sign(Options options)
    {
        var file = options.Require("file");
        var version = options.Require("version");
        var hash = Sha256(file);
        using var key = LoadPrivateKey(Path.GetFullPath(options.Require("private")));
        var signature = UpdateSignature.Sign(key, version, hash);
        var output = options.Get("out") ?? file + UpdateSignature.AssetSuffix;
        File.WriteAllText(output, signature);
        Console.WriteLine($"已签名：{output}");
        Console.WriteLine($"  版本 {version.TrimStart('v', 'V')}，SHA-256 {hash}");
        return 0;
    }

    public static int Verify(Options options)
    {
        var file = options.Require("file");
        var version = options.Require("version");
        var signature = File.ReadAllText(options.Require("sig"));
        var keys = options.Get("public") is { } one
            ? [one]
            : TrustFile.ReadKeys(TrustFile.Locate(options.Get("trust-file")));
        if (keys.Count == 0)
        {
            Console.Error.WriteLine("没有可用的公钥：UpdateTrust.cs 里还没有受信公钥，请先 keygen，或用 --public 指定。");
            return 3;
        }

        var ok = UpdateSignature.Verify(version, Sha256(file), signature, keys);
        Console.WriteLine(ok ? "签名有效。" : "签名无效：安装包、版本号或签名与受信公钥对不上。");
        return ok ? 0 : 1;
    }

    public static int Status(Options options)
    {
        var path = TrustFile.Locate(options.Get("trust-file"));
        var keys = TrustFile.ReadKeys(path);
        Console.WriteLine(keys.Count == 0
            ? "未启用签名强制：UpdateTrust.cs 里没有受信公钥，更新只做 SHA-256 校验。运行 keygen 启用。"
            : $"已启用签名强制：{keys.Count} 把受信公钥。更新必须带有效签名。");
        return keys.Count == 0 ? 3 : 0;
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ECDsa LoadPrivateKey(string path)
    {
        var pem = File.ReadAllText(path);
        var key = ECDsa.Create();
        if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            key.ImportFromEncryptedPem(pem, Secrets.ReadPassword("私钥密码："));
        }
        else
        {
            key.ImportFromPem(pem);
        }

        return key;
    }
}
