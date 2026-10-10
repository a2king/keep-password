namespace KeepPassword.Core.Crypto;

/// <summary>
/// Argon2id 参数。写入保险库文件，三个系统使用同一套参数解释。
/// </summary>
public sealed record KdfProfile(int MemoryKb, int Iterations, int Parallelism)
{
    public static KdfProfile Interactive { get; } = new(65536, 3, 1);

    /// <summary>单元测试用的轻量参数，算法仍是 Argon2id。</summary>
    public static KdfProfile Fast { get; } = new(4096, 1, 1);

    public const int MaxMemoryKb = 262_144;
    public const int MaxIterations = 32;
    public const int MaxParallelism = 8;

    public void Validate()
    {
        if (MemoryKb is < 8 or > MaxMemoryKb)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryKb));
        }

        if (Iterations is < 1 or > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations));
        }

        if (Parallelism is < 1 or > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism));
        }
    }
}
