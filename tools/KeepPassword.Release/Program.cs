namespace KeepPassword.Release;

/// <summary>
/// 发布者工具：生成更新签名密钥、给安装包签名、验证签名、检查当前是否启用了签名强制。
/// 私钥只在这台机器上用，程序里只内置公钥（Core/Update/UpdateTrust.cs）。
/// </summary>
internal static class Program
{
    private const string Usage = """
        用法：
          keygen  --private <私钥文件.keppriv.pem> [--trust-file <UpdateTrust.cs>] [--no-password]
                  生成签名密钥。私钥默认用密码加密保存；公钥自动写入 UpdateTrust.cs。
          sign    --private <私钥文件> --file <安装包> --version <版本号> [--out <签名文件>]
                  对安装包签名，输出 <安装包>.sig。密码从环境变量 KP_SIGN_PASSWORD 读取，没有则交互输入。
          verify  --file <安装包> --version <版本号> --sig <签名文件> [--public <公钥base64>]
                  用内置公钥（或指定公钥）验证签名。
          status  [--trust-file <UpdateTrust.cs>]
                  显示是否已配置受信公钥。已配置返回 0，未配置返回 3。
        """;

    public static int Main(string[] args)
    {
        // 中文提示在 GBK 控制台里会乱码，统一按 UTF-8 输出。
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1));
            return args[0] switch
            {
                "keygen" => Commands.KeyGen(options),
                "sign" => Commands.Sign(options),
                "verify" => Commands.Verify(options),
                "status" => Commands.Status(options),
                _ => Fail($"未知命令：{args[0]}\n\n{Usage}")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.Cryptography.CryptographicException
                                       or UnauthorizedAccessException or FormatException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
