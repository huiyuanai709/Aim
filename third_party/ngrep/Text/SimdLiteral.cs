using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Ngrep.Text;

/// <summary>
/// Width chosen for the byte prefilter on this process.
/// AVX-512 is used only when <see cref="Avx512BW.IsSupported"/> is true, so a
/// machine without it stays on AVX2, SSE / Vector128, or scalar.
/// </summary>
internal enum SimdIsa
{
    Scalar,
    Vector128,
    Avx2,
    Avx512,
}

/// <summary>
/// Multi-literal byte search. One needle uses a memmem-style compare of the
/// first, rarest interior, and last bytes. Several needles of length at least
/// two use a Teddy nibble fingerprint (a bit per pattern, confirmed exactly).
/// A SIMD compare nominates alignments; an exact compare accepts them.
/// </summary>
internal sealed partial class SimdLiteralScanner
{
    private readonly byte[][] _needles;
    private readonly bool _ignoreCase;
    private readonly bool _empty;
    private readonly int _minLength;
    private readonly byte[]? _one;
    private readonly byte _first;
    private readonly byte _firstAlt;
    private readonly byte _last;
    private readonly byte _lastAlt;
    private readonly bool _firstFold;
    private readonly bool _lastFold;
    private readonly int[] _offs;
    private readonly int[] _ids;
    private readonly byte[] _probes;
    private readonly Vector128<byte>[] _k128;
    private readonly Vector256<byte>[] _k256;
    private readonly Vector512<byte>[] _k512;
    private readonly bool _useRare;
    private readonly int _rareAt;
    private readonly byte _rare;
    private readonly byte _rareAlt;
    private readonly bool _rareFold;
    private readonly bool _covered;
    private readonly int _filterAt;
    private readonly byte _filter;
    private readonly byte _filterAlt;
    private readonly bool _filterFold;
    private readonly TeddyTables? _teddy;

    private SimdLiteralScanner(byte[][] needles, bool ignoreCase)
    {
        _ignoreCase = ignoreCase;
        _needles = needles;
        _one = null;
        _empty = false;
        foreach (var needle in needles)
        {
            if (needle.Length == 0)
            {
                _empty = true;
                break;
            }
        }

        if (_empty || needles.Length == 0)
        {
            _minLength = 0;
            _offs = [];
            _ids = [];
            _probes = [];
            _k128 = [];
            _k256 = [];
            _k512 = [];
            return;
        }

        _minLength = needles.Min(static needle => needle.Length);
        if (needles.Length == 1 && needles[0].Length >= 2)
        {
            _one = needles[0];
            _first = _one[0];
            _last = _one[^1];
            _firstFold = IsAsciiLetter(_first);
            _lastFold = IsAsciiLetter(_last);
            _firstAlt = (byte)(_first ^ 0x20);
            _lastAlt = (byte)(_last ^ 0x20);
            _rareAt = PickRare(_one);
            _useRare = _rareAt > 0 && _rareAt < _one.Length - 1;
            if (_useRare)
            {
                _rare = _one[_rareAt];
                _rareFold = IsAsciiLetter(_rare);
                _rareAlt = (byte)(_rare ^ 0x20);
            }

            // Length 2 is fully covered by the end bytes. Length 3 with the middle byte is too.
            _covered = _one.Length <= 2 || (_useRare && _one.Length == 3);
            _filterAt = 0;
            _filter = _first;
            _filterAlt = _firstAlt;
            _filterFold = _firstFold;
            var best = Rank(_first);
            if (_useRare)
            {
                var rareRank = Rank(_rare);
                if (rareRank < best)
                {
                    best = rareRank;
                    _filterAt = _rareAt;
                    _filter = _rare;
                    _filterAlt = _rareAlt;
                    _filterFold = _rareFold;
                }
            }

            if (Rank(_last) < best)
            {
                _filterAt = _one.Length - 1;
                _filter = _last;
                _filterAlt = _lastAlt;
                _filterFold = _lastFold;
            }
        }

        var lists = new List<int>[256];
        for (var n = 0; n < needles.Length; n++)
        {
            AddStart(lists, needles[n][0], n);
            if (ignoreCase && IsAsciiLetter(needles[n][0]))
            {
                AddStart(lists, (byte)(needles[n][0] ^ 0x20), n);
            }
        }

        var ids = new List<int>();
        _offs = new int[257];
        var probes = new List<byte>();
        for (var b = 0; b < 256; b++)
        {
            _offs[b] = ids.Count;
            var list = lists[b];
            if (list == null)
            {
                continue;
            }

            probes.Add((byte)b);
            ids.AddRange(list);
        }

        _offs[256] = ids.Count;
        _ids = ids.ToArray();
        _probes = probes.ToArray();
        if (needles.Length >= 2 && _minLength >= 2)
        {
            _teddy = TeddyTables.Build(needles, ignoreCase);
        }

        if (_one != null)
        {
            _k128 = [];
            _k256 = [];
            _k512 = [];
            return;
        }

        _k128 = new Vector128<byte>[_probes.Length];
        _k256 = new Vector256<byte>[_probes.Length];
        _k512 = new Vector512<byte>[_probes.Length];
        for (var i = 0; i < _probes.Length; i++)
        {
            _k128[i] = Vector128.Create(_probes[i]);
            _k256[i] = Vector256.Create(_probes[i]);
            _k512[i] = Vector512.Create(_probes[i]);
        }
    }

    public static SimdIsa Active =>
        Avx512BW.IsSupported ? SimdIsa.Avx512 :
        Avx2.IsSupported ? SimdIsa.Avx2 :
        Sse2.IsSupported || Vector128.IsHardwareAccelerated ? SimdIsa.Vector128 :
        SimdIsa.Scalar;

    public static bool Supports(SimdIsa isa) => isa switch
    {
        SimdIsa.Scalar => true,
        SimdIsa.Vector128 => Sse2.IsSupported || Vector128.IsHardwareAccelerated,
        SimdIsa.Avx2 => Avx2.IsSupported,
        SimdIsa.Avx512 => Avx512BW.IsSupported,
        _ => false,
    };

    public static SimdLiteralScanner Create(IReadOnlyList<byte[]> needles, bool ignoreCase)
    {
        var copy = new byte[needles.Count][];
        for (var i = 0; i < needles.Count; i++)
        {
            var needle = needles[i] ?? throw new ArgumentNullException(nameof(needles));
            copy[i] = needle;
        }

        return new SimdLiteralScanner(copy, ignoreCase);
    }

    public int IndexOfAny(ReadOnlySpan<byte> haystack) => IndexOfAny(haystack, Active);

    public int IndexOfAny(ReadOnlySpan<byte> haystack, SimdIsa isa)
    {
        if (_empty)
        {
            return 0;
        }

        if (_needles.Length == 0 || haystack.Length < _minLength)
        {
            return -1;
        }

        return isa switch
        {
            SimdIsa.Avx512 => ScanAvx512(haystack),
            SimdIsa.Avx2 => ScanAvx2(haystack),
            SimdIsa.Vector128 => Scan128(haystack),
            _ => IndexOfAnyScalar(haystack, 0),
        };
    }

    public bool ContainsAny(ReadOnlySpan<byte> haystack) => IndexOfAny(haystack) >= 0;

    private int ScanAvx512(ReadOnlySpan<byte> haystack)
    {
        if (!Avx512BW.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        return _one != null ? ScanOneAvx512(haystack) : ScanManyAvx512(haystack);
    }

    private int ScanAvx2(ReadOnlySpan<byte> haystack)
    {
        if (!Avx2.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        return _one != null ? ScanOneAvx2(haystack) : ScanManyAvx2(haystack);
    }

    private int Scan128(ReadOnlySpan<byte> haystack)
    {
        if (Sse2.IsSupported)
        {
            return _one != null ? ScanOneSse2(haystack) : ScanManySse2(haystack);
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return _one != null ? ScanOneVector128(haystack) : ScanManyVector128(haystack);
        }

        return IndexOfAnyScalar(haystack, 0);
    }

    private unsafe int ScanOneAvx512(ReadOnlySpan<byte> haystack)
    {
        if (!Avx512BW.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var needle = _one!;
        var len = needle.Length;
        var lastVec = haystack.Length - len - 63;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var first = Vector512.Create(_first);
            var firstAlt = Vector512.Create(_firstAlt);
            var last = Vector512.Create(_last);
            var lastAlt = Vector512.Create(_lastAlt);
            var rare = Vector512.Create(_rare);
            var rareAlt = Vector512.Create(_rareAlt);
            var filter = Vector512.Create(_filter);
            var filterAlt = Vector512.Create(_filterAlt);
            var filterAt = _filterAt;
            var i = 0;
            if (lastVec >= 0)
            {
                for (; i <= lastVec; i += 64)
                {
                    // One compare of the rarest probe byte. A miss skips the other loads.
                    var primary = Avx512F.LoadVector512(basePtr + i + filterAt);
                    var pm = Avx512BW.CompareEqual(primary, filter);
                    if (_ignoreCase && _filterFold)
                    {
                        pm = Avx512F.Or(pm, Avx512BW.CompareEqual(primary, filterAlt));
                    }

                    if (pm.ExtractMostSignificantBits() == 0)
                    {
                        continue;
                    }

                    var head = Avx512F.LoadVector512(basePtr + i);
                    var tail = Avx512F.LoadVector512(basePtr + i + len - 1);
                    var hm = Avx512BW.CompareEqual(head, first);
                    if (_ignoreCase && _firstFold)
                    {
                        hm = Avx512F.Or(hm, Avx512BW.CompareEqual(head, firstAlt));
                    }

                    var tm = Avx512BW.CompareEqual(tail, last);
                    if (_ignoreCase && _lastFold)
                    {
                        tm = Avx512F.Or(tm, Avx512BW.CompareEqual(tail, lastAlt));
                    }

                    var mask = Avx512F.And(hm, tm);
                    if (_useRare)
                    {
                        var mid = Avx512F.LoadVector512(basePtr + i + _rareAt);
                        var rm = Avx512BW.CompareEqual(mid, rare);
                        if (_ignoreCase && _rareFold)
                        {
                            rm = Avx512F.Or(rm, Avx512BW.CompareEqual(mid, rareAlt));
                        }

                        mask = Avx512F.And(mask, rm);
                    }

                    var found = ConfirmBits(haystack, i, mask.ExtractMostSignificantBits(), endsMatched: true);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int ScanManyAvx512(ReadOnlySpan<byte> haystack)
    {
        if (!Avx512BW.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        if (_teddy != null)
        {
            return SearchTeddyAvx512(haystack);
        }

        var keys = _k512;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var i = 0;
            var last = haystack.Length - 64;
            if (last >= 0)
            {
                for (; i <= last; i += 64)
                {
                    var chunk = Avx512F.LoadVector512(basePtr + i);
                    var acc = Vector512<byte>.Zero;
                    for (var p = 0; p < keys.Length; p++)
                    {
                        acc = Avx512F.Or(acc, Avx512BW.CompareEqual(chunk, keys[p]));
                    }

                    var found = ConfirmBits(haystack, i, acc.ExtractMostSignificantBits(), endsMatched: false);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int ScanOneAvx2(ReadOnlySpan<byte> haystack)
    {
        if (!Avx2.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var needle = _one!;
        var len = needle.Length;
        var lastVec = haystack.Length - len - 31;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var first = Vector256.Create(_first);
            var firstAlt = Vector256.Create(_firstAlt);
            var last = Vector256.Create(_last);
            var lastAlt = Vector256.Create(_lastAlt);
            var rare = Vector256.Create(_rare);
            var rareAlt = Vector256.Create(_rareAlt);
            var filter = Vector256.Create(_filter);
            var filterAlt = Vector256.Create(_filterAlt);
            var filterAt = _filterAt;
            var i = 0;
            if (lastVec >= 0)
            {
                for (; i <= lastVec; i += 32)
                {
                    var primary = Avx.LoadVector256(basePtr + i + filterAt);
                    var pm = Avx2.CompareEqual(primary, filter);
                    if (_ignoreCase && _filterFold)
                    {
                        pm = Avx2.Or(pm, Avx2.CompareEqual(primary, filterAlt));
                    }

                    if ((uint)Avx2.MoveMask(pm) == 0)
                    {
                        continue;
                    }

                    var head = Avx.LoadVector256(basePtr + i);
                    var tail = Avx.LoadVector256(basePtr + i + len - 1);
                    var hm = Avx2.CompareEqual(head, first);
                    if (_ignoreCase && _firstFold)
                    {
                        hm = Avx2.Or(hm, Avx2.CompareEqual(head, firstAlt));
                    }

                    var tm = Avx2.CompareEqual(tail, last);
                    if (_ignoreCase && _lastFold)
                    {
                        tm = Avx2.Or(tm, Avx2.CompareEqual(tail, lastAlt));
                    }

                    var mask = Avx2.And(hm, tm);
                    if (_useRare)
                    {
                        var mid = Avx.LoadVector256(basePtr + i + _rareAt);
                        var rm = Avx2.CompareEqual(mid, rare);
                        if (_ignoreCase && _rareFold)
                        {
                            rm = Avx2.Or(rm, Avx2.CompareEqual(mid, rareAlt));
                        }

                        mask = Avx2.And(mask, rm);
                    }

                    var found = ConfirmBits(haystack, i, (uint)Avx2.MoveMask(mask), endsMatched: true);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int ScanManyAvx2(ReadOnlySpan<byte> haystack)
    {
        if (!Avx2.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        if (_teddy != null)
        {
            return SearchTeddyAvx2(haystack);
        }

        var keys = _k256;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var i = 0;
            var last = haystack.Length - 32;
            if (last >= 0)
            {
                for (; i <= last; i += 32)
                {
                    var chunk = Avx.LoadVector256(basePtr + i);
                    var acc = Vector256<byte>.Zero;
                    for (var p = 0; p < keys.Length; p++)
                    {
                        acc = Avx2.Or(acc, Avx2.CompareEqual(chunk, keys[p]));
                    }

                    var found = ConfirmBits(haystack, i, (uint)Avx2.MoveMask(acc), endsMatched: false);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int ScanOneSse2(ReadOnlySpan<byte> haystack)
    {
        if (!Sse2.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        var needle = _one!;
        var len = needle.Length;
        var lastVec = haystack.Length - len - 15;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var first = Vector128.Create(_first);
            var firstAlt = Vector128.Create(_firstAlt);
            var last = Vector128.Create(_last);
            var lastAlt = Vector128.Create(_lastAlt);
            var rare = Vector128.Create(_rare);
            var rareAlt = Vector128.Create(_rareAlt);
            var filter = Vector128.Create(_filter);
            var filterAlt = Vector128.Create(_filterAlt);
            var filterAt = _filterAt;
            var i = 0;
            if (lastVec >= 0)
            {
                for (; i <= lastVec; i += 16)
                {
                    var primary = Sse2.LoadVector128(basePtr + i + filterAt);
                    var pm = Sse2.CompareEqual(primary, filter);
                    if (_ignoreCase && _filterFold)
                    {
                        pm = Sse2.Or(pm, Sse2.CompareEqual(primary, filterAlt));
                    }

                    if ((uint)Sse2.MoveMask(pm) == 0)
                    {
                        continue;
                    }

                    var head = Sse2.LoadVector128(basePtr + i);
                    var tail = Sse2.LoadVector128(basePtr + i + len - 1);
                    var hm = Sse2.CompareEqual(head, first);
                    if (_ignoreCase && _firstFold)
                    {
                        hm = Sse2.Or(hm, Sse2.CompareEqual(head, firstAlt));
                    }

                    var tm = Sse2.CompareEqual(tail, last);
                    if (_ignoreCase && _lastFold)
                    {
                        tm = Sse2.Or(tm, Sse2.CompareEqual(tail, lastAlt));
                    }

                    var mask = Sse2.And(hm, tm);
                    if (_useRare)
                    {
                        var mid = Sse2.LoadVector128(basePtr + i + _rareAt);
                        var rm = Sse2.CompareEqual(mid, rare);
                        if (_ignoreCase && _rareFold)
                        {
                            rm = Sse2.Or(rm, Sse2.CompareEqual(mid, rareAlt));
                        }

                        mask = Sse2.And(mask, rm);
                    }

                    var found = ConfirmBits(haystack, i, (uint)Sse2.MoveMask(mask), endsMatched: true);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private unsafe int ScanManySse2(ReadOnlySpan<byte> haystack)
    {
        if (!Sse2.IsSupported)
        {
            return IndexOfAnyScalar(haystack, 0);
        }

        if (_teddy != null && Ssse3.IsSupported)
        {
            return SearchTeddySse(haystack);
        }

        var keys = _k128;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        fixed (byte* basePtr = &origin)
        {
            var i = 0;
            var last = haystack.Length - 16;
            if (last >= 0)
            {
                for (; i <= last; i += 16)
                {
                    var chunk = Sse2.LoadVector128(basePtr + i);
                    var acc = Vector128<byte>.Zero;
                    for (var p = 0; p < keys.Length; p++)
                    {
                        acc = Sse2.Or(acc, Sse2.CompareEqual(chunk, keys[p]));
                    }

                    var found = ConfirmBits(haystack, i, (uint)Sse2.MoveMask(acc), endsMatched: false);
                    if (found >= 0)
                    {
                        return found;
                    }
                }
            }

            return IndexOfAnyScalar(haystack, i);
        }
    }

    private int ScanOneVector128(ReadOnlySpan<byte> haystack)
    {
        var needle = _one!;
        var len = needle.Length;
        var lastVec = haystack.Length - len - 15;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        var first = Vector128.Create(_first);
        var firstAlt = Vector128.Create(_firstAlt);
        var last = Vector128.Create(_last);
        var lastAlt = Vector128.Create(_lastAlt);
        var rare = Vector128.Create(_rare);
        var rareAlt = Vector128.Create(_rareAlt);
        var filter = Vector128.Create(_filter);
        var filterAlt = Vector128.Create(_filterAlt);
        var filterAt = _filterAt;
        var i = 0;
        if (lastVec >= 0)
        {
            for (; i <= lastVec; i += 16)
            {
                var primary = Vector128.LoadUnsafe(ref origin, (nuint)(i + filterAt));
                var pm = Vector128.Equals(primary, filter);
                if (_ignoreCase && _filterFold)
                {
                    pm = Vector128.BitwiseOr(pm, Vector128.Equals(primary, filterAlt));
                }

                if (pm.ExtractMostSignificantBits() == 0)
                {
                    continue;
                }

                var head = Vector128.LoadUnsafe(ref origin, (nuint)i);
                var tail = Vector128.LoadUnsafe(ref origin, (nuint)(i + len - 1));
                var hm = Vector128.Equals(head, first);
                if (_ignoreCase && _firstFold)
                {
                    hm = Vector128.BitwiseOr(hm, Vector128.Equals(head, firstAlt));
                }

                var tm = Vector128.Equals(tail, last);
                if (_ignoreCase && _lastFold)
                {
                    tm = Vector128.BitwiseOr(tm, Vector128.Equals(tail, lastAlt));
                }

                var mask = Vector128.BitwiseAnd(hm, tm);
                if (_useRare)
                {
                    var mid = Vector128.LoadUnsafe(ref origin, (nuint)(i + _rareAt));
                    var rm = Vector128.Equals(mid, rare);
                    if (_ignoreCase && _rareFold)
                    {
                        rm = Vector128.BitwiseOr(rm, Vector128.Equals(mid, rareAlt));
                    }

                    mask = Vector128.BitwiseAnd(mask, rm);
                }

                var found = ConfirmBits(haystack, i, mask.ExtractMostSignificantBits(), endsMatched: true);
                if (found >= 0)
                {
                    return found;
                }
            }
        }

        return IndexOfAnyScalar(haystack, i);
    }

    private int ScanManyVector128(ReadOnlySpan<byte> haystack)
    {
        if (_teddy != null && Ssse3.IsSupported)
        {
            return SearchTeddySse(haystack);
        }

        var keys = _k128;
        ref var origin = ref MemoryMarshal.GetReference(haystack);
        var i = 0;
        var last = haystack.Length - 16;
        if (last >= 0)
        {
            for (; i <= last; i += 16)
            {
                var chunk = Vector128.LoadUnsafe(ref origin, (nuint)i);
                var acc = Vector128<byte>.Zero;
                for (var p = 0; p < keys.Length; p++)
                {
                    acc = Vector128.BitwiseOr(acc, Vector128.Equals(chunk, keys[p]));
                }

                var found = ConfirmBits(haystack, i, acc.ExtractMostSignificantBits(), endsMatched: false);
                if (found >= 0)
                {
                    return found;
                }
            }
        }

        return IndexOfAnyScalar(haystack, i);
    }

    private int ConfirmBits(ReadOnlySpan<byte> haystack, int origin, ulong bits, bool endsMatched)
    {
        while (bits != 0)
        {
            var bit = BitOperations.TrailingZeroCount(bits);
            var pos = origin + bit;
            if (endsMatched)
            {
                if (_covered || MatchesAt(haystack, pos, _one!))
                {
                    return pos;
                }
            }
            else if (MatchesAny(haystack, pos))
            {
                return pos;
            }

            bits &= bits - 1;
        }

        return -1;
    }

    internal int IndexOfAnyScalar(ReadOnlySpan<byte> haystack, int start)
    {
        if (_empty)
        {
            return 0;
        }

        if (_needles.Length == 0 || start > haystack.Length - _minLength)
        {
            return -1;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (_one != null)
        {
            return IndexOfOne(haystack, start);
        }

        var last = haystack.Length - _minLength;
        for (var i = start; i <= last; i++)
        {
            if (MatchesAny(haystack, i))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfOne(ReadOnlySpan<byte> haystack, int start)
    {
        var needle = _one!;
        var len = needle.Length;
        var last = haystack.Length - len;
        for (var i = start; i <= last; i++)
        {
            if (!Probe(haystack[i + _filterAt], _filter, _filterAlt, _filterFold))
            {
                continue;
            }

            if (_filterAt != 0 && !Probe(haystack[i], _first, _firstAlt, _firstFold))
            {
                continue;
            }

            if (_filterAt != len - 1 && !Probe(haystack[i + len - 1], _last, _lastAlt, _lastFold))
            {
                continue;
            }

            if (_useRare && _filterAt != _rareAt && !Probe(haystack[i + _rareAt], _rare, _rareAlt, _rareFold))
            {
                continue;
            }

            if (_covered || MatchesAt(haystack, i, needle))
            {
                return i;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Probe(byte value, byte expected, byte alt, bool fold) =>
        value == expected || (_ignoreCase && fold && value == alt);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchesAny(ReadOnlySpan<byte> haystack, int pos)
    {
        var b = haystack[pos];
        var begin = _offs[b];
        var end = _offs[b + 1];
        for (var k = begin; k < end; k++)
        {
            if (MatchesAt(haystack, pos, _needles[_ids[k]]))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchesAt(ReadOnlySpan<byte> haystack, int pos, byte[] needle)
    {
        var len = needle.Length;
        if ((uint)pos + (uint)len > (uint)haystack.Length)
        {
            return false;
        }

        if (!_ignoreCase)
        {
            return haystack.Slice(pos, len).SequenceEqual(needle);
        }

        for (var j = 0; j < len; j++)
        {
            if (!AsciiEqual(haystack[pos + j], needle[j]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AsciiEqual(byte left, byte right)
    {
        if (left == right)
        {
            return true;
        }

        if (left is >= (byte)'A' and <= (byte)'Z')
        {
            left += 32;
        }

        if (right is >= (byte)'A' and <= (byte)'Z')
        {
            right += 32;
        }

        return left == right;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    /// <summary>Lowest-frequency interior byte. Common ASCII letters confirm far more often than a rare byte.</summary>
    private static int PickRare(byte[] needle)
    {
        if (needle.Length < 3)
        {
            return 0;
        }

        var best = 1;
        var bestRank = Rank(needle[1]);
        for (var i = 2; i < needle.Length - 1; i++)
        {
            var rank = Rank(needle[i]);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = i;
            }
        }

        return best;
    }

    private static int Rank(byte value)
    {
        if (value is >= (byte)'A' and <= (byte)'Z')
        {
            value += 32;
        }

        // Rough English / source frequency. Lower is rarer and a better filter.
        return value switch
        {
            (byte)'e' or (byte)'t' or (byte)'a' or (byte)'o' or (byte)'i' or (byte)'n' or (byte)'s' or (byte)' ' => 40,
            (byte)'r' or (byte)'h' or (byte)'l' or (byte)'d' or (byte)'c' or (byte)'u' or (byte)'m' => 30,
            (byte)'_' or (byte)'.' or (byte)'/' or (byte)'\\' or (byte)'(' or (byte)')' or (byte)'"' or (byte)'\'' => 24,
            >= (byte)'0' and <= (byte)'9' => 18,
            >= (byte)'a' and <= (byte)'z' => 12,
            _ => value < 32 ? 1 : 4,
        };
    }

    private static void AddStart(List<int>[] lists, byte value, int needle)
    {
        var list = lists[value];
        if (list == null)
        {
            list = new List<int>(1);
            lists[value] = list;
        }

        list.Add(needle);
    }
}

/// <summary>Memchr-style scan for UTF-16 newlines. Line numbers stay on the decoded string.</summary>
internal static class SimdNewline
{
    public static void Append(ReadOnlySpan<char> text, List<int> starts) =>
        Append(text, starts, Pick(text.Length));

    internal static void Append(ReadOnlySpan<char> text, List<int> starts, SimdIsa isa)
    {
        if (text.IsEmpty)
        {
            return;
        }

        if (isa == SimdIsa.Avx512 && Avx512BW.IsSupported && text.Length >= 32)
        {
            AppendAvx512(text, starts);
            return;
        }

        if (isa == SimdIsa.Avx2 && Avx2.IsSupported && text.Length >= 16)
        {
            AppendAvx2(text, starts);
            return;
        }

        if (isa == SimdIsa.Vector128 && (Sse2.IsSupported || Vector128.IsHardwareAccelerated) && text.Length >= 8)
        {
            Append128(text, starts);
            return;
        }

        AppendScalar(text, 0, starts);
    }

    private static SimdIsa Pick(int length)
    {
        if (Avx512BW.IsSupported && length >= 32)
        {
            return SimdIsa.Avx512;
        }

        if (Avx2.IsSupported && length >= 16)
        {
            return SimdIsa.Avx2;
        }

        if ((Sse2.IsSupported || Vector128.IsHardwareAccelerated) && length >= 8)
        {
            return SimdIsa.Vector128;
        }

        return SimdIsa.Scalar;
    }

    private static unsafe void AppendAvx512(ReadOnlySpan<char> text, List<int> starts)
    {
        if (!Avx512BW.IsSupported)
        {
            AppendScalar(text, 0, starts);
            return;
        }

        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        fixed (ushort* basePtr = &origin)
        {
            var nl = Vector512.Create((ushort)'\n');
            var i = 0;
            var last = text.Length - 32;
            for (; i <= last; i += 32)
            {
                var chunk = Avx512F.LoadVector512(basePtr + i);
                var bits = Avx512BW.CompareEqual(chunk, nl).ExtractMostSignificantBits();
                while (bits != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(bits);
                    starts.Add(i + bit + 1);
                    bits &= bits - 1;
                }
            }

            AppendScalar(text, i, starts);
        }
    }

    private static unsafe void AppendAvx2(ReadOnlySpan<char> text, List<int> starts)
    {
        if (!Avx2.IsSupported)
        {
            AppendScalar(text, 0, starts);
            return;
        }

        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        fixed (ushort* basePtr = &origin)
        {
            var nl = Vector256.Create((ushort)'\n');
            var i = 0;
            var last = text.Length - 16;
            for (; i <= last; i += 16)
            {
                var chunk = Avx.LoadVector256(basePtr + i);
                var bits = (uint)Avx2.CompareEqual(chunk, nl).ExtractMostSignificantBits();
                while (bits != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(bits);
                    starts.Add(i + bit + 1);
                    bits &= bits - 1;
                }
            }

            AppendScalar(text, i, starts);
        }
    }

    private static void Append128(ReadOnlySpan<char> text, List<int> starts)
    {
        if (Sse2.IsSupported)
        {
            AppendSse2(text, starts);
            return;
        }

        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        var nl = Vector128.Create((ushort)'\n');
        var i = 0;
        var last = text.Length - 8;
        for (; i <= last; i += 8)
        {
            var chunk = Vector128.LoadUnsafe(ref origin, (nuint)i);
            var bits = Vector128.Equals(chunk, nl).ExtractMostSignificantBits();
            while (bits != 0)
            {
                var bit = BitOperations.TrailingZeroCount(bits);
                starts.Add(i + bit + 1);
                bits &= bits - 1;
            }
        }

        AppendScalar(text, i, starts);
    }

    private static unsafe void AppendSse2(ReadOnlySpan<char> text, List<int> starts)
    {
        if (!Sse2.IsSupported)
        {
            AppendScalar(text, 0, starts);
            return;
        }

        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        fixed (ushort* basePtr = &origin)
        {
            var nl = Vector128.Create((ushort)'\n');
            var i = 0;
            var last = text.Length - 8;
            for (; i <= last; i += 8)
            {
                var chunk = Sse2.LoadVector128(basePtr + i);
                var eq = Sse2.CompareEqual(chunk, nl);
                var bits = (uint)Sse2.MoveMask(eq.AsByte());
                while (bits != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(bits);
                    if ((bit & 1) == 0)
                    {
                        starts.Add(i + (bit >> 1) + 1);
                    }

                    bits &= bits - 1;
                }
            }

            AppendScalar(text, i, starts);
        }
    }

    private static void AppendScalar(ReadOnlySpan<char> text, int start, List<int> starts)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
    }
}
