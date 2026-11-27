using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Ngrep.Text;

/// <summary>
/// Next byte that belongs to a 256-bit class. A handful of members is a SIMD
/// equality. A handful of ranges is an unsigned compare. Denser sets stay scalar.
/// </summary>
internal sealed class ClassFinder
{
    private enum Kind : byte
    {
        None,
        Scalar,
        Members,
        Ranges,
    }

    private readonly Kind _kind;
    private readonly int _memberCount;
    private readonly byte _b0, _b1, _b2, _b3, _b4, _b5, _b6, _b7;
    private readonly int _rangeCount;
    private readonly byte _lo0, _hi0, _lo1, _hi1, _lo2, _hi2;
    private readonly ulong _m0, _m1, _m2, _m3;

    private ClassFinder(
        Kind kind,
        int memberCount,
        byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7,
        int rangeCount,
        byte lo0, byte hi0, byte lo1, byte hi1, byte lo2, byte hi2,
        ulong m0, ulong m1, ulong m2, ulong m3)
    {
        _kind = kind;
        _memberCount = memberCount;
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _b3 = b3;
        _b4 = b4;
        _b5 = b5;
        _b6 = b6;
        _b7 = b7;
        _rangeCount = rangeCount;
        _lo0 = lo0;
        _hi0 = hi0;
        _lo1 = lo1;
        _hi1 = hi1;
        _lo2 = lo2;
        _hi2 = hi2;
        _m0 = m0;
        _m1 = m1;
        _m2 = m2;
        _m3 = m3;
    }

    public static ClassFinder Create(ulong m0, ulong m1, ulong m2, ulong m3)
    {
        Span<byte> members = stackalloc byte[8];
        var memberCount = 0;
        var rangeCount = 0;
        byte lo0 = 0, hi0 = 0, lo1 = 0, hi1 = 0, lo2 = 0, hi2 = 0;
        var inRun = false;
        byte runLo = 0;
        for (var value = 0; value < 256; value++)
        {
            var present = MaskHas(m0, m1, m2, m3, (byte)value);
            if (present)
            {
                if (memberCount < 8)
                {
                    members[memberCount] = (byte)value;
                }

                memberCount++;
                if (!inRun)
                {
                    inRun = true;
                    runLo = (byte)value;
                }
            }
            else if (inRun)
            {
                inRun = false;
                PushRange(ref rangeCount, ref lo0, ref hi0, ref lo1, ref hi1, ref lo2, ref hi2, runLo, (byte)(value - 1));
            }
        }

        if (inRun)
        {
            PushRange(ref rangeCount, ref lo0, ref hi0, ref lo1, ref hi1, ref lo2, ref hi2, runLo, 255);
        }

        if (memberCount == 0)
        {
            return new ClassFinder(Kind.None, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, m0, m1, m2, m3);
        }

        // A few inclusive ranges compile to a tight compare loop. Single-byte
        // ranges ([abc]) take that path too: the member loop's per-byte branches
        // were several times slower on a miss.
        if (rangeCount is >= 1 and <= 3)
        {
            return new ClassFinder(Kind.Ranges, 0, 0, 0, 0, 0, 0, 0, 0, 0, rangeCount, lo0, hi0, lo1, hi1, lo2, hi2, m0, m1, m2, m3);
        }

        if (memberCount <= 8)
        {
            return new ClassFinder(
                Kind.Members,
                memberCount,
                members[0],
                memberCount > 1 ? members[1] : (byte)0,
                memberCount > 2 ? members[2] : (byte)0,
                memberCount > 3 ? members[3] : (byte)0,
                memberCount > 4 ? members[4] : (byte)0,
                memberCount > 5 ? members[5] : (byte)0,
                memberCount > 6 ? members[6] : (byte)0,
                memberCount > 7 ? members[7] : (byte)0,
                0, 0, 0, 0, 0, 0, 0,
                m0, m1, m2, m3);
        }

        return new ClassFinder(Kind.Scalar, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, m0, m1, m2, m3);
    }

    public int RangeCount => _kind == Kind.Ranges ? _rangeCount : 0;

    public bool TryRanges(out int count, out byte lo0, out byte hi0, out byte lo1, out byte hi1, out byte lo2, out byte hi2)
    {
        if (_kind != Kind.Ranges)
        {
            count = 0;
            lo0 = hi0 = lo1 = hi1 = lo2 = hi2 = 0;
            return false;
        }

        count = _rangeCount;
        lo0 = _lo0;
        hi0 = _hi0;
        lo1 = _lo1;
        hi1 = _hi1;
        lo2 = _lo2;
        hi2 = _hi2;
        return true;
    }

    public int Next(ReadOnlySpan<byte> text, int start)
    {
        if ((uint)start >= (uint)text.Length || _kind == Kind.None)
        {
            return text.Length;
        }

        if (_kind == Kind.Members)
        {
            if (Avx512BW.IsSupported && text.Length - start >= 64)
            {
                return NextMembers512(text, start);
            }

            if (Avx2.IsSupported && text.Length - start >= 32)
            {
                return NextMembers256(text, start);
            }

            if (Sse2.IsSupported && text.Length - start >= 16)
            {
                return NextMembers128(text, start);
            }
        }
        else if (_kind == Kind.Ranges)
        {
            if (Avx512BW.IsSupported && text.Length - start >= 64)
            {
                return NextRanges512(text, start);
            }

            if (Avx2.IsSupported && text.Length - start >= 32)
            {
                return NextRanges256(text, start);
            }

            if (Sse2.IsSupported && text.Length - start >= 16)
            {
                return NextRanges128(text, start);
            }
        }

        return NextScalar(text, start);
    }

    private unsafe int NextMembers512(ReadOnlySpan<byte> text, int start)
    {
        if (!Avx512BW.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var v0 = Vector512.Create(_b0);
            var v1 = Vector512.Create(_b1);
            var v2 = Vector512.Create(_b2);
            var v3 = Vector512.Create(_b3);
            var v4 = Vector512.Create(_b4);
            var v5 = Vector512.Create(_b5);
            var v6 = Vector512.Create(_b6);
            var v7 = Vector512.Create(_b7);
            var i = start;
            var last = text.Length - 64;
            for (; i <= last; i += 64)
            {
                var chunk = Avx512F.LoadVector512(basePtr + i);
                var mask = Avx512BW.CompareEqual(chunk, v0);
                if (_memberCount > 1)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v1));
                }

                if (_memberCount > 2)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v2));
                }

                if (_memberCount > 3)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v3));
                }

                if (_memberCount > 4)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v4));
                }

                if (_memberCount > 5)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v5));
                }

                if (_memberCount > 6)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v6));
                }

                if (_memberCount > 7)
                {
                    mask = Avx512F.Or(mask, Avx512BW.CompareEqual(chunk, v7));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    private unsafe int NextMembers256(ReadOnlySpan<byte> text, int start)
    {
        if (!Avx2.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var v0 = Vector256.Create(_b0);
            var v1 = Vector256.Create(_b1);
            var v2 = Vector256.Create(_b2);
            var v3 = Vector256.Create(_b3);
            var v4 = Vector256.Create(_b4);
            var v5 = Vector256.Create(_b5);
            var v6 = Vector256.Create(_b6);
            var v7 = Vector256.Create(_b7);
            var i = start;
            var last = text.Length - 32;
            for (; i <= last; i += 32)
            {
                var chunk = Avx.LoadVector256(basePtr + i);
                var mask = Avx2.CompareEqual(chunk, v0);
                if (_memberCount > 1)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v1));
                }

                if (_memberCount > 2)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v2));
                }

                if (_memberCount > 3)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v3));
                }

                if (_memberCount > 4)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v4));
                }

                if (_memberCount > 5)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v5));
                }

                if (_memberCount > 6)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v6));
                }

                if (_memberCount > 7)
                {
                    mask = Avx2.Or(mask, Avx2.CompareEqual(chunk, v7));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    private unsafe int NextMembers128(ReadOnlySpan<byte> text, int start)
    {
        if (!Sse2.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var v0 = Vector128.Create(_b0);
            var v1 = Vector128.Create(_b1);
            var v2 = Vector128.Create(_b2);
            var v3 = Vector128.Create(_b3);
            var v4 = Vector128.Create(_b4);
            var v5 = Vector128.Create(_b5);
            var v6 = Vector128.Create(_b6);
            var v7 = Vector128.Create(_b7);
            var i = start;
            var last = text.Length - 16;
            for (; i <= last; i += 16)
            {
                var chunk = Sse2.LoadVector128(basePtr + i);
                var mask = Sse2.CompareEqual(chunk, v0);
                if (_memberCount > 1)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v1));
                }

                if (_memberCount > 2)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v2));
                }

                if (_memberCount > 3)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v3));
                }

                if (_memberCount > 4)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v4));
                }

                if (_memberCount > 5)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v5));
                }

                if (_memberCount > 6)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v6));
                }

                if (_memberCount > 7)
                {
                    mask = Sse2.Or(mask, Sse2.CompareEqual(chunk, v7));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    private unsafe int NextRanges512(ReadOnlySpan<byte> text, int start)
    {
        if (!Avx512BW.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector512.Create((byte)0x80);
            var i = start;
            var last = text.Length - 64;
            for (; i <= last; i += 64)
            {
                var chunk = Avx512F.Xor(Avx512F.LoadVector512(basePtr + i), bias);
                var mask = Range512(chunk, _lo0, _hi0);
                if (_rangeCount > 1)
                {
                    mask = Avx512F.Or(mask, Range512(chunk, _lo1, _hi1));
                }

                if (_rangeCount > 2)
                {
                    mask = Avx512F.Or(mask, Range512(chunk, _lo2, _hi2));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    private unsafe int NextRanges256(ReadOnlySpan<byte> text, int start)
    {
        if (!Avx2.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector256.Create((byte)0x80);
            var i = start;
            var last = text.Length - 32;
            for (; i <= last; i += 32)
            {
                var chunk = Avx2.Xor(Avx.LoadVector256(basePtr + i), bias);
                var mask = Range256(chunk, _lo0, _hi0);
                if (_rangeCount > 1)
                {
                    mask = Avx2.Or(mask, Range256(chunk, _lo1, _hi1));
                }

                if (_rangeCount > 2)
                {
                    mask = Avx2.Or(mask, Range256(chunk, _lo2, _hi2));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    private unsafe int NextRanges128(ReadOnlySpan<byte> text, int start)
    {
        if (!Sse2.IsSupported)
        {
            return NextScalar(text, start);
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector128.Create((byte)0x80);
            var i = start;
            var last = text.Length - 16;
            for (; i <= last; i += 16)
            {
                var chunk = Sse2.Xor(Sse2.LoadVector128(basePtr + i), bias);
                var mask = Range128(chunk, _lo0, _hi0);
                if (_rangeCount > 1)
                {
                    mask = Sse2.Or(mask, Range128(chunk, _lo1, _hi1));
                }

                if (_rangeCount > 2)
                {
                    mask = Sse2.Or(mask, Range128(chunk, _lo2, _hi2));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits != 0)
                {
                    return i + BitOperations.TrailingZeroCount(bits);
                }
            }

            return NextScalar(text, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector512<byte> Range512(Vector512<byte> biased, byte lo, byte hi)
    {
        var loV = Vector512.Create((byte)(lo ^ 0x80));
        var hiV = Vector512.Create((byte)(hi ^ 0x80));
        // Byte CompareGreaterThan is unsigned. The 0x80 bias is a signed order, so compare as sbyte.
        var ge = Avx512F.Or(Avx512BW.CompareEqual(biased, loV), Avx512BW.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
        var le = Avx512F.Or(Avx512BW.CompareEqual(biased, hiV), Avx512BW.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
        return Avx512F.And(ge, le);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<byte> Range256(Vector256<byte> biased, byte lo, byte hi)
    {
        var loV = Vector256.Create((byte)(lo ^ 0x80));
        var hiV = Vector256.Create((byte)(hi ^ 0x80));
        var ge = Avx2.Or(Avx2.CompareEqual(biased, loV), Avx2.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
        var le = Avx2.Or(Avx2.CompareEqual(biased, hiV), Avx2.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
        return Avx2.And(ge, le);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Range128(Vector128<byte> biased, byte lo, byte hi)
    {
        var loV = Vector128.Create((byte)(lo ^ 0x80));
        var hiV = Vector128.Create((byte)(hi ^ 0x80));
        var ge = Sse2.Or(Sse2.CompareEqual(biased, loV), Sse2.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
        var le = Sse2.Or(Sse2.CompareEqual(biased, hiV), Sse2.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
        return Sse2.And(ge, le);
    }

    private int NextScalar(ReadOnlySpan<byte> text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (MaskHas(_m0, _m1, _m2, _m3, text[i]))
            {
                return i;
            }
        }

        return text.Length;
    }

    private static bool MaskHas(ulong m0, ulong m1, ulong m2, ulong m3, byte value)
    {
        var bit = 1UL << (value & 63);
        return value switch
        {
            < 64 => (m0 & bit) != 0,
            < 128 => (m1 & bit) != 0,
            < 192 => (m2 & bit) != 0,
            _ => (m3 & bit) != 0,
        };
    }

    private static void PushRange(
        ref int rangeCount,
        ref byte lo0, ref byte hi0,
        ref byte lo1, ref byte hi1,
        ref byte lo2, ref byte hi2,
        byte lo, byte hi)
    {
        if (rangeCount == 0)
        {
            lo0 = lo;
            hi0 = hi;
        }
        else if (rangeCount == 1)
        {
            lo1 = lo;
            hi1 = hi;
        }
        else if (rangeCount == 2)
        {
            lo2 = lo;
            hi2 = hi;
        }

        rangeCount++;
    }
}
