---
name: dotnet-11-libraries
description: Use when writing or reviewing C#/.NET code that could use a .NET 11 base library API, such as span-based ZLib/Deflate/GZip/Zstandard decoders and encoders, ReadOnlyMemoryStream, MemoryExtensions.Min/Max, Vector128 Unzip/Zip/Reverse, BitArray span constructors, Utf16 validation and RunePosition, and when the project also targets net10.0. Covers exact signatures, status results, the per-thread decoder pattern, the Compat helper rule that keeps `#if NET11_0_OR_GREATER` out of feature code, and managed MD5 for FIPS systems. Checked against the 11.0.0-rc.1 SDK and reference pack.
---

# .NET 11 base library additions

Everything here was checked against SDK `11.0.100-rc.1` (runtime `11.0.0-rc.1`), the `Microsoft.NETCore.App.Ref` packs for 10.0.12 and 11.0.0-rc.1, and probe apps run on both runtimes. Re-check after the final release. Default to `net10.0;net11.0` targets (see `dotnet-multi-targeting`), so each API below needs a `net10.0` fallback.

## The Compat helper rule

Keep every `#if NET11_0_OR_GREATER` branch inside one small static class in a `Compat/` folder. Both branches sit behind the same signature. Feature code calls the helper and never contains `#if`.

| Do | Do not |
| --- | --- |
| `ZLibCompat.Inflate(source, dest, out c, out w)` in feature code | `#if NET11_0_OR_GREATER` around a decoder call in a parser |
| One Compat file per API family | One large `Compat.cs` that grows without limit |
| Same status results on both branches, or document the gap in the doc comment | A fallback that silently behaves differently |
| Cover both branches with the same tests | Testing only the `net11.0` leg |

[`ZLibCompat.cs`](ZLibCompat.cs) is a working example. Verified on both runtimes:

| Case | `net11.0` (ZLibDecoder) | `net10.0` (ZLibStream fallback) |
| --- | --- | --- |
| Valid data | `Done` | `Done` |
| Corrupt data | `InvalidData` | `InvalidData` |
| Destination too small | `DestinationTooSmall`, partial output kept | `DestinationTooSmall`, partial output kept |
| Truncated data | `NeedMoreData`, partial output kept | `Done` (cannot tell truncated from complete) |
| Allocation per call, steady state | 0 bytes | about 312 bytes (stream objects) |

The truncated row is a real difference. If truncation matters on `net10.0`, compare the output length with a size you know, such as the expected row bytes of an image.

## Span-based compression (new in .NET 11)

All of these are in `System.IO.Compression`. None exist on `net10.0`.

| Type | Shape |
| --- | --- |
| `ZLibDecoder`, `DeflateDecoder`, `GZipDecoder` | `sealed class`, `IDisposable`, parameterless constructor |
| Members | `OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)`, `void Reset()`, `static bool TryDecompress(ReadOnlySpan<byte>, Span<byte>, out int bytesWritten)` |
| `ZLibEncoder`, `DeflateEncoder`, `GZipEncoder` | `sealed class`, `IDisposable`. Constructors: `()`, `(int quality)`, `(int quality, int windowLog2)`, `(ZLibCompressionOptions)` |
| Encoder members | `OperationStatus Compress(ReadOnlySpan<byte>, Span<byte>, out int consumed, out int written, bool isFinalBlock)`, `Flush(Span<byte>, out int written)`, `Reset()`, `static long GetMaxCompressedLength(long)`, `static bool TryCompress(...)` with optional quality and windowLog2 |
| `ZLibCompressionOptions` | New `WindowLog2` property, plus `DefaultWindowLog2` (15), `MinWindowLog2` (8) and `MaxWindowLog2` (15) |
| Zstandard | `ZstandardEncoder`, `ZstandardDecoder` (same shape, plus `SetPrefix`, `TryGetMaxDecompressedLength`), `ZstandardStream`, `ZstandardDictionary` (`Create`, `Train`), `ZstandardCompressionOptions`, `ZstandardDecompressionOptions` |

`TryDecompress` and `TryCompress` are **static**, so calling them on an instance is error CS0176. `TryDecompress` writes nothing and returns `false` when the destination is short, so use the instance `Decompress` when the output size is unknown.

Status results, measured:

| Input | `Decompress` returns | Exception |
| --- | --- | --- |
| Complete valid stream | `Done` | none |
| First half of a stream | `NeedMoreData`, decoded prefix is in the destination | none |
| Bad zlib header | `InvalidData` | none |
| Bad Adler-32 trailer | `InvalidData`, but all output was already written | none |
| Destination too small | `DestinationTooSmall`, `bytesConsumed` and `bytesWritten` show progress | none |

After `DestinationTooSmall` you can continue with `source[consumed..]` and a destination starting at `written` without calling `Reset()`. A grow-and-retry loop with `ArrayPool<byte>` is therefore straightforward (see `InflateToArray` in the example).

### Per-thread decoder, zero steady-state allocation

Create the decoder once per thread and call `Reset()` before each stream. 1000 decodes of an 8000-byte output allocated 0 bytes. A fresh `new ZLibDecoder()` per call allocated about 144 bytes each, plus native state.

```csharp
[ThreadStatic]
private static ZLibDecoder? _decoder;

ZLibDecoder decoder = _decoder ??= new ZLibDecoder();
decoder.Reset();
OperationStatus status = decoder.Decompress(source, destination, out int consumed, out int written);
```

`[ThreadStatic]` instances are never disposed. That is acceptable for a long-lived worker thread; use a pool of decoders if threads come and go.

Zstandard has no mandated place in container formats such as PDF. Do not add it to a reader without a format reason.

## In-memory streams

| Type | Namespace | Use |
| --- | --- | --- |
| `ReadOnlyMemoryStream(ReadOnlyMemory<byte>)` | `System.IO` | Read-only (`CanWrite` is false). `Write` throws `NotSupportedException` |
| `WritableMemoryStream(Memory<byte>)` | `System.IO` | Fixed size. Writing past the end throws `NotSupportedException` |
| `ReadOnlySequenceStream(ReadOnlySequence<byte>)` | `System.Buffers` | Wraps a pipe's segments without flattening |

`StringStream` does **not** exist in 11.0.0-rc.1. Do not use it.

## Small additions

| API | Notes (verified) |
| --- | --- |
| `MemoryExtensions.Min<T>(ReadOnlySpan<T>)` and `Max`, with an `IComparer<T>` overload | An empty span throws `InvalidOperationException`. Guard with `IsEmpty` |
| `Vector128/256/512.Unzip`, `UnzipEven`, `UnzipOdd`, `Zip`, `ZipLower`, `ZipUpper`, `ConcatLowerLower`, `ConcatLowerUpper`, `ConcatUpperLower`, `ConcatUpperUpper`, `Reverse`, `CreateAlternatingSequence` | Also on `Vector64`. `Unzip` returns a tuple of two vectors. Unzipping the bytes 0 to 31 gives the evens `0,2,..30` and the odds `1,3,..31`. There is no `Concat` or `ConcatLower` method. On `net10.0` use `Vector128.Shuffle` with constant indices |
| `BitArray(ReadOnlySpan<byte>)`, `BitArray(ReadOnlySpan<bool>)`, `BitArray(ReadOnlySpan<int>)`, `PopCount()` | **LSB first.** Byte `0x01` sets `[0]`, byte `0x80` sets `[7]`. Formats that number bits from the most significant bit (PDF and TIFF bitmaps) read the wrong bits. Read those with shifts: `(b >> (7 - i)) & 1` |
| `Utf16.IsValid(ReadOnlySpan<char>)`, `Utf16.IndexOfInvalidSubsequence`, `Utf8.IndexOfInvalidSubsequence` | New in 11. `Utf8.IsValid` is older (.NET 8). A lone high surrogate reports the index of the bad char |
| `RunePosition.EnumerateUtf8(ReadOnlySpan<byte>)`, `EnumerateUtf16(ReadOnlySpan<char>)` | Yields `Rune`, `StartIndex`, `Length` and `WasReplaced` per code point. `"aé😀"u8` gives starts 0, 1, 3 and lengths 1, 2, 4 |
| `CryptographicOperations.FixedTimeEquals(ReadOnlySpan<byte>, byte)` | New in 11: compares every byte with one value. The span-to-span overload is old |
| `Base64.EncodeToUtf8`, `DecodeFromUtf8` and `TryDecodeFromUtf8` convenience overloads | New in 11 in `System.Buffers.Text` |

## Crypto notes

- `MD5.HashData` goes through the platform crypto library. On Linux with OpenSSL in FIPS mode MD5 can be refused. This is reported behaviour; this host does not run FIPS, so it was not reproduced. If a format mandates MD5 (PDF RC4 and AES-128 key derivation), use a managed implementation. [`ManagedMd5.cs`](ManagedMd5.cs) is about 100 lines of RFC 1321, allocation-free in Release builds, and matched `MD5.HashData` on the RFC test strings and on random inputs of 0 to 599 bytes. Report a FIPS refusal as a clear error, not a crash.
- Compare password hashes and MACs with `CryptographicOperations.FixedTimeEquals`, never `SequenceEqual`.
- RC4 is not in the base library. Write it in managed code (about 30 lines).

## Checklist

- [ ] Every `#if NET11_0_OR_GREATER` branch lives in a `Compat/` helper with one signature for both branches
- [ ] Differences between the two branches are in the helper's doc comment and covered by tests on both TFMs
- [ ] Decoders and encoders are cached per thread and reset, not created per call
- [ ] Output size unknown: instance `Decompress` in a grow loop, not `TryDecompress`
- [ ] `DestinationTooSmall`, `NeedMoreData` and `InvalidData` handled as results, not exceptions
- [ ] No `StringStream`, and no `BitArray` span constructor on MSB-first data
- [ ] MD5 behind a managed fallback where FIPS systems are in scope
