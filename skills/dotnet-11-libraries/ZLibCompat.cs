// Compat/ZLibCompat.cs: the only file that knows about the .NET 11 decoder.
// Feature code calls ZLibCompat.Inflate and never contains #if.
using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Compat;

internal static class ZLibCompat
{
#if NET11_0_OR_GREATER
    // One decoder per thread: Reset() makes it reusable, so steady-state decoding allocates nothing.
    [ThreadStatic]
    private static ZLibDecoder? _decoder;
#endif

    /// <summary>
    /// Inflates one zlib stream. Returns Done, DestinationTooSmall (written holds the partial output),
    /// InvalidData, or NeedMoreData for truncated input (.NET 11 only; the .NET 10 path cannot tell
    /// truncated input from a complete stream and returns Done with the bytes it decoded).
    /// </summary>
    public static OperationStatus Inflate(ReadOnlyMemory<byte> source, Span<byte> destination, out int consumed, out int written)
    {
#if NET11_0_OR_GREATER
        ZLibDecoder decoder = _decoder ??= new ZLibDecoder();
        decoder.Reset();
        return decoder.Decompress(source.Span, destination, out consumed, out written);
#else
        // ZLibStream needs a Stream. Wrap the caller's array without copying when it has one.
        MemoryStream input = MemoryMarshal.TryGetArray(source, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(source.ToArray(), writable: false);
        using (input)
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        {
            written = 0;
            consumed = 0;
            try
            {
                while (written < destination.Length)
                {
                    int read = zlib.Read(destination[written..]);
                    if (read == 0)
                    {
                        consumed = (int)input.Position;
                        return OperationStatus.Done;
                    }

                    written += read;
                }

                // Destination is full: one more byte tells "exactly full" from "too small".
                consumed = (int)input.Position;
                Span<byte> probe = stackalloc byte[1];
                return zlib.Read(probe) == 0 ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
            }
            catch (InvalidDataException)
            {
                consumed = (int)input.Position;
                return OperationStatus.InvalidData;
            }
        }
#endif
    }

    /// <summary>Grow-and-retry loop over pooled buffers. Returns the decoded bytes or throws on corrupt data.</summary>
    public static byte[] InflateToArray(ReadOnlyMemory<byte> source)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, source.Length * 4));
        try
        {
            while (true)
            {
                OperationStatus status = Inflate(source, buffer, out _, out int written);
                if (status == OperationStatus.Done)
                {
                    return buffer.AsSpan(0, written).ToArray();
                }

                if (status != OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidDataException($"The zlib stream is {status}.");
                }

                ArrayPool<byte>.Shared.Return(buffer);
                buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
