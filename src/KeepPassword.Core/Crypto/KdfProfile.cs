namespace KeepPassword.Core.Crypto;

/// <summary>
/// Argon2id 参数。写入保险库文件，三个系统使用同一套参数解释。
/// </summary>
public sealed record KdfProfile(int MemoryKb, int Iterations, int Parallelism)
{
    public static KdfProfile Interactive { get; } = new(65536, 3, 1);

    /// <summary>单元测试用的轻量参数，算法仍是 Argon2id。</summary>
    public static KdfProfile Fast { get; } = new(4096, 1, 1);

    public void Validate()
    {
        if (MemoryKb < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryKb));
        }

        if (Iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations));
        }

        if (Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism));
        }
    }
}
