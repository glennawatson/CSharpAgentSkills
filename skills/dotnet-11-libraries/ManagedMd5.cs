// Managed MD5 (RFC 1321) for formats that mandate MD5 where System.Security.Cryptography.MD5
// may be refused, such as OpenSSL in FIPS mode. Verified against MD5.HashData on the RFC test
// strings and on random inputs of length 0 to 299.
using System.Buffers.Binary;
using System.Numerics;

namespace Compat;

internal static class ManagedMd5
{
    public const int HashSize = 16;

    private static ReadOnlySpan<int> Shifts =>
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    // K[i] = floor(2^32 * abs(sin(i + 1))), written out so no floating point runs at startup.
    private static ReadOnlySpan<uint> Constants =>
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    /// <summary>Hashes <paramref name="source"/> into the first 16 bytes of <paramref name="destination"/>. Allocates nothing.</summary>
    public static void HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, HashSize);

        // The last one or two blocks hold the leftover bytes, the 0x80 marker and the bit length.
        Span<byte> tail = stackalloc byte[128];
        int full = source.Length / 64 * 64;
        int remainder = source.Length - full;
        int tailLength = remainder < 56 ? 64 : 128;
        source[full..].CopyTo(tail);
        tail[remainder] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(tail[(tailLength - 8)..], (ulong)source.Length * 8);

        Span<uint> state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
        Span<uint> words = stackalloc uint[16];
        for (int offset = 0; offset < full; offset += 64)
        {
            Transform(source.Slice(offset, 64), words, state);
        }

        for (int offset = 0; offset < tailLength; offset += 64)
        {
            Transform(tail.Slice(offset, 64), words, state);
        }

        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * 4)..], state[i]);
        }
    }

    private static void Transform(ReadOnlySpan<byte> block, Span<uint> words, Span<uint> state)
    {
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
        }

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (int i = 0; i < 64; i++)
        {
            uint f;
            int g;
            if (i < 16)
            {
                f = (b & c) | (~b & d);
                g = i;
            }
            else if (i < 32)
            {
                f = (d & b) | (~d & c);
                g = ((5 * i) + 1) & 15;
            }
            else if (i < 48)
            {
                f = b ^ c ^ d;
                g = ((3 * i) + 5) & 15;
            }
            else
            {
                f = c ^ (b | ~d);
                g = (7 * i) & 15;
            }

            f += a + Constants[i] + words[g];
            a = d;
            d = c;
            c = b;
            b += BitOperations.RotateLeft(f, Shifts[i]);
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
    }
}
