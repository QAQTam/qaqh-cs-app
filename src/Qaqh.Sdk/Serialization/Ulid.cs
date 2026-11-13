using System.Security.Cryptography;

namespace Qaqh.Sdk.Serialization;

/// <summary>
/// ULID（Crockford Base32，26 字符：48bit 毫秒时间戳 + 80bit 随机）。
/// 对齐 qaqh-ringing-contract 的 <c>ulid::generate_ulid</c>：命令 id 只要求
/// 唯一性与字典序可排，随机生成即可满足。
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static string NewUlid()
    {
        Span<byte> bytes = stackalloc byte[16];
        lock (Rng)
        {
            Rng.GetBytes(bytes);
        }

        // 48bit 时间戳（ms）占据 128bit 的最高位（前 6 字节）。
        ulong millis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(millis >> 40);
        bytes[1] = (byte)(millis >> 32);
        bytes[2] = (byte)(millis >> 24);
        bytes[3] = (byte)(millis >> 16);
        bytes[4] = (byte)(millis >> 8);
        bytes[5] = (byte)millis;

        // 130 = 26×5：从最高位逐 5bit 取出，超出 128bit 的尾位补 0。
        Span<char> chars = stackalloc char[26];
        for (int i = 0; i < 26; i++)
        {
            int value = 0;
            for (int bit = 0; bit < 5; bit++)
            {
                value = (value << 1) | ReadBit(bytes, i * 5 + bit);
            }
            chars[i] = Alphabet[value];
        }
        return new string(chars);
    }

    private static int ReadBit(scoped ReadOnlySpan<byte> data, int index)
    {
        int byteIndex = index >> 3;
        if (byteIndex >= data.Length)
        {
            return 0;
        }
        return (data[byteIndex] >> (7 - (index & 7))) & 1;
    }
}
