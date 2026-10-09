using System.Buffers.Binary;
using System.Text;

namespace KeepPassword.Core.Messaging;

public static class NativeMessageFraming
{
    public const int MaxBytes = 1024 * 1024;

    public static void Write(Stream stream, string json)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(json);
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxBytes)
        {
            throw new InvalidOperationException("消息过长。");
        }

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Flush();
    }

    public static string? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!ReadExact(stream, header))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxBytes)
        {
            throw new InvalidDataException("消息长度无效。");
        }

        var body = new byte[length];
        if (!ReadExact(stream, body))
        {
            return null;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
