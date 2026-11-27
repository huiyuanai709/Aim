using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Ngrep.Text;

internal sealed partial class SimdLiteralScanner
{
    /// <summary>
    /// Multi-pattern fingerprint. Each pattern owns one bit in a byte. A pair of
    /// nibble shuffles (the Teddy lookup) tests several leading bytes at once.
    /// Shuffle tables are repeated in every 16-byte lane, so AVX2 and AVX-512
    /// runs stay correct without crossing those lanes. Candidates are confirmed
    /// against the full needle, including ASCII case folding.
    /// </summary>
    private sealed class TeddyTables
    {
        private TeddyTables(
            int fingerprint,
            int groups,
            Vector128<byte>[] lo128,
            Vector128<byte>[] hi128,
            Vector256<byte>[] lo256,
            Vector256<byte>[] hi256,
            Vector512<byte>[] lo512,
            Vector512<byte>[] hi512)
        {
            Fingerprint = fingerprint;
            Groups = groups;
            Lo128 = lo128;
            Hi128 = hi128;
            Lo256 = lo256;
            Hi256 = hi256;
            Lo512 = lo512;
            Hi512 = hi512;
        }

        public int Fingerprint { get; }

        public int Groups { get; }

        public Vector128<byte>[] Lo128 { get; }

        public Vector128<byte>[] Hi128 { get; }

        public Vector256<byte>[] Lo256 { get; }

        public Vector256<byte>[] Hi256 { get; }

        public Vector512<byte>[] Lo512 { get; }

        public Vector512<byte>[] Hi512 { get; }

        public static TeddyTables Build(byte[][] needles, bool ignoreCase)
        {
            var fingerprint = Math.Min(3, needles.Min(static needle => needle.Length));
            var groups = (needles.Length + 7) / 8;
            var slots = groups * fingerprint;
            var loBytes = new byte[slots * 16];
            var hiBytes = new byte[slots * 16];
            for (var n = 0; n < needles.Length; n++)
            {
                var bit = (byte)(1 << (n & 7));
                var group = n >> 3;
                var needle = needles[n];
                for (var f = 0; f < fingerprint; f++)
                {
                    var at = (group * fingerprint + f) * 16;
                    Stamp(loBytes, hiBytes, at, needle[f], bit);
                    if (ignoreCase && IsAsciiLetter(needle[f]))
                    {
                        Stamp(loBytes, hiBytes, at, (byte)(needle[f] ^ 0x20), bit);
                    }
                }
            }

            var lo128 = new Vector128<byte>[slots];
            var hi128 = new Vector128<byte>[slots];
            var lo256 = new Vector256<byte>[slots];
            var hi256 = new Vector256<byte>[slots];
            var lo512 = new Vector512<byte>[slots];
            var hi512 = new Vector512<byte>[slots];
            for (var i = 0; i < slots; i++)
            {
                var lo = Vector128.Create(loBytes.AsSpan(i * 16, 16));
                var hi = Vector128.Create(hiBytes.AsSpan(i * 16, 16));
                lo128[i] = lo;
                hi128[i] = hi;
                // Broadcast into every 16-byte lane. vpshufb does not cross lanes.
                lo256[i] = Vector256.Create(lo);
                hi256[i] = Vector256.Create(hi);
                lo512[i] = Vector512.Create(lo);
                hi512[i] = Vector512.Create(hi);
            }

            return new TeddyTables(fingerprint, groups, lo128, hi128, lo256, hi256, lo512, hi512);
        }

        private static void Stamp(byte[] lo, byte[] hi, int at, byte value, byte bit)
        {
            lo[at + (value & 0x0F)] |= bit;
            hi[at + (value >> 4)] |= bit;
        }
    }

    private unsafe int SearchTeddyAvx512(ReadOnlySpan<byte> haystack)
    {
        if (!Avx512BW.IsSupported || _teddy == null)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var teddy = _teddy;
        const int width = 64;
        var fp = teddy.Fingerprint;
        var limit = haystack.Length - fp - (width - 1);
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var low = Vector512.Create((byte)0x0F);
            var clear = Vector512.Create((ushort)0x0F0F);
            Span<byte> masks = stackalloc byte[width];
            var i = 0;
            if (limit >= 0)
            {
                for (; i <= limit; i += width)
                {
                    var best = -1;
                    for (var g = 0; g < teddy.Groups; g++)
                    {
                        var acc = Vector512<byte>.AllBitsSet;
                        for (var f = 0; f < fp; f++)
                        {
                            var chunk = Avx512F.LoadVector512(basePtr + i + f);
                            var loNib = Avx512F.And(chunk, low);
                            var hiNib = Avx512F.And(Avx512BW.ShiftRightLogical(chunk.AsUInt16(), 4), clear).AsByte();
                            var slot = g * fp + f;
                            var looked = Avx512F.And(
                                Avx512BW.Shuffle(teddy.Lo512[slot], loNib),
                                Avx512BW.Shuffle(teddy.Hi512[slot], hiNib));
                            acc = Avx512F.And(acc, looked);
                        }

                        var zeroBits = Avx512BW.CompareEqual(acc, Vector512<byte>.Zero).ExtractMostSignificantBits();
                        acc.StoreUnsafe(ref MemoryMarshal.GetReference(masks));
                        var found = ConfirmTeddy(haystack, i, ~zeroBits, masks, g * 8);
                        if (found >= 0 && (best < 0 || found < best))
                        {
                            best = found;
                        }
                    }

                    if (best >= 0)
                    {
                        return best;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int SearchTeddyAvx2(ReadOnlySpan<byte> haystack)
    {
        if (!Avx2.IsSupported || _teddy == null)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var teddy = _teddy;
        const int width = 32;
        var fp = teddy.Fingerprint;
        var limit = haystack.Length - fp - (width - 1);
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var low = Vector256.Create((byte)0x0F);
            var clear = Vector256.Create((ushort)0x0F0F);
            Span<byte> masks = stackalloc byte[width];
            var i = 0;
            if (limit >= 0)
            {
                for (; i <= limit; i += width)
                {
                    var best = -1;
                    for (var g = 0; g < teddy.Groups; g++)
                    {
                        var acc = Vector256<byte>.AllBitsSet;
                        for (var f = 0; f < fp; f++)
                        {
                            var chunk = Avx.LoadVector256(basePtr + i + f);
                            var loNib = Avx2.And(chunk, low);
                            var hiNib = Avx2.And(Avx2.ShiftRightLogical(chunk.AsUInt16(), 4), clear).AsByte();
                            var slot = g * fp + f;
                            var looked = Avx2.And(
                                Avx2.Shuffle(teddy.Lo256[slot], loNib),
                                Avx2.Shuffle(teddy.Hi256[slot], hiNib));
                            acc = Avx2.And(acc, looked);
                        }

                        var zeroBits = (uint)Avx2.MoveMask(Avx2.CompareEqual(acc, Vector256<byte>.Zero));
                        acc.StoreUnsafe(ref MemoryMarshal.GetReference(masks));
                        var found = ConfirmTeddy(haystack, i, ~zeroBits, masks, g * 8);
                        if (found >= 0 && (best < 0 || found < best))
                        {
                            best = found;
                        }
                    }

                    if (best >= 0)
                    {
                        return best;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int SearchTeddySse(ReadOnlySpan<byte> haystack)
    {
        if (!Ssse3.IsSupported || _teddy == null)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var teddy = _teddy;
        const int width = 16;
        var fp = teddy.Fingerprint;
        var limit = haystack.Length - fp - (width - 1);
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var low = Vector128.Create((byte)0x0F);
            var clear = Vector128.Create((ushort)0x0F0F);
            Span<byte> masks = stackalloc byte[width];
            var i = 0;
            if (limit >= 0)
            {
                for (; i <= limit; i += width)
                {
                    var best = -1;
                    for (var g = 0; g < teddy.Groups; g++)
                    {
                        var acc = Vector128<byte>.AllBitsSet;
                        for (var f = 0; f < fp; f++)
                        {
                            var chunk = Sse2.LoadVector128(basePtr + i + f);
                            var loNib = Sse2.And(chunk, low);
                            var hiNib = Sse2.And(Sse2.ShiftRightLogical(chunk.AsUInt16(), 4), clear).AsByte();
                            var slot = g * fp + f;
                            var looked = Sse2.And(
                                Ssse3.Shuffle(teddy.Lo128[slot], loNib),
                                Ssse3.Shuffle(teddy.Hi128[slot], hiNib));
                            acc = Sse2.And(acc, looked);
                        }

                        var zeroBits = (uint)Sse2.MoveMask(Sse2.CompareEqual(acc, Vector128<byte>.Zero));
                        acc.StoreUnsafe(ref MemoryMarshal.GetReference(masks));
                        var found = ConfirmTeddy(haystack, i, (~zeroBits) & 0xFFFF, masks, g * 8);
                        if (found >= 0 && (best < 0 || found < best))
                        {
                            best = found;
                        }
                    }

                    if (best >= 0)
                    {
                        return best;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private int ConfirmTeddy(ReadOnlySpan<byte> haystack, int origin, ulong bits, ReadOnlySpan<byte> masks, int needleBase)
    {
        while (bits != 0)
        {
            var bit = BitOperations.TrailingZeroCount(bits);
            if ((uint)bit >= (uint)masks.Length)
            {
                break;
            }

            var mask = masks[bit];
            var pos = origin + bit;
            while (mask != 0)
            {
                var pattern = needleBase + BitOperations.TrailingZeroCount(mask);
                if ((uint)pattern < (uint)_needles.Length && MatchesAt(haystack, pos, _needles[pattern]))
                {
                    return pos;
                }

                mask &= (byte)(mask - 1);
            }

            bits &= bits - 1;
        }

        return -1;
    }
}
