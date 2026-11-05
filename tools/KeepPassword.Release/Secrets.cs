using System.Text;

namespace KeepPassword.Release;

internal static class Secrets
{
    public const string PasswordVariable = "KP_SIGN_PASSWORD";

    /// <summary>优先读环境变量（方便 CI / 脚本），否则在终端里不回显地读取。</summary>
    public static string ReadPassword(string prompt, bool confirm = false)
    {
        var fromEnv = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        if (Console.IsInputRedirected)
        {
            throw new ArgumentException($"当前不是交互终端，请设置环境变量 {PasswordVariable}。");
        }

        var first = Prompt(prompt);
        if (confirm && Prompt("再输入一次确认：") != first)
        {
            throw new ArgumentException("两次输入的密码不一致。");
        }

        if (first.Length < 8)
        {
            throw new ArgumentException("私钥密码至少 8 位。");
        }

        return first;
    }

    private static string Prompt(string text)
    {
        Console.Error.Write(text);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
