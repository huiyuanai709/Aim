using System.Numerics;

namespace Ngrep.Search;

/// <summary>
/// A Pike VM with a determinized byte automaton in front of it, used only as a
/// "could this text match at all" filter. A Pike VM walks every byte with a thread list,
/// about 0.015 GB/s, and the shapes that reach it without a literal prefix are the slowest
/// ones (<c>[a-z]+[0-9]+</c>, <c>^[a-z]+[0-9]+$</c>, <c>[a-f][0-9][a-f]</c>). One table
/// lookup per byte settles a miss, so the VM then only runs when a match exists.
/// <para>
/// The filter never decides spans. The VM still produces them, so leftmost-first, greedy,
/// and lazy results are the same as before. Anchors that depend on the surrounding bytes
/// (<c>^ $ \A \z \b \B</c>) are part of the automaton state, because their truth depends on
/// the previous byte. <c>\Z</c> also depends on whether the current byte is the last one,
/// which a forward scan cannot know, so a pattern with <c>\Z</c> keeps the VM alone.
/// A pattern that matches the empty string is not wrapped either, because the filter would
/// always say yes.
/// </para>
/// </summary>
internal sealed class DfaProgram : ByteProgram
{
    /// <summary>State cap for the eager determinization. Past this the VM is used alone.</summary>
    private const int MaxStates = 256;

    private readonly PikeProgram _inner;
    private readonly DfaTable _filter;

    private DfaProgram(PikeProgram inner, DfaTable filter, bool lineSafe)
    {
        _inner = inner;
        _filter = filter;
        LineSafe = lineSafe;
    }

    /// <summary>
    /// True when the pattern has no <c>\A</c> or <c>\z</c>, so confirming one line at a
    /// time sees the same text the whole-file engine would.
    /// </summary>
    public bool LineSafe { get; }

    /// <summary>
    /// Wraps <paramref name="pike"/> when the filter is worth building. Null keeps the VM
    /// alone, which is what a literal prefix, <c>\Z</c>, or an oversized automaton needs.
    /// </summary>
    public static ByteProgram? Wrap(PikeProgram pike, PikeProgram.Inst[] insts, int unanchored, byte[]? prefix)
    {
        // A literal prefix already lets the VM scan with SIMD instead of a thread list.
        if (prefix is { Length: >= 2 } || insts.Length == 0 || insts.Length > 64)
        {
            return null;
        }

        foreach (var inst in insts)
        {
            // \Z is true before a trailing newline, so it needs one byte of lookahead.
            if (inst.Op == PikeProgram.Op.Eow)
            {
                return null;
            }
        }

        var lineSafe = true;
        foreach (var inst in insts)
        {
            if (inst.Op is PikeProgram.Op.Bos or PikeProgram.Op.Eos)
            {
                lineSafe = false;
                break;
            }
        }

        var filter = DfaTable.TryBuild(insts, unanchored, MaxStates);
        if (filter == null || filter.AlwaysAccepts)
        {
            return null;
        }

        return new DfaProgram(pike, filter, lineSafe);
    }

    /// <summary>
    /// Forwarded so the plan still narrows to candidate lines before running the VM. The
    /// filter does not change which engine confirms, only whether a miss reaches it.
    /// </summary>
    public override bool PrefersLiteralPrefilter => _inner.PrefersLiteralPrefilter;

    /// <summary>Forwarded: the VM is only worth running when it can scan with a prefix.</summary>
    public override bool PrefersDecoded => _inner.PrefersDecoded;

    /// <summary>Whether any match is possible in these bytes. Exact for this instruction subset.</summary>
    public bool MayMatch(ReadOnlySpan<byte> bytes) => _filter.MayMatch(bytes);

    /// <summary>
    /// Line starts where a match can end, in order. The filter is exact, so each returned
    /// line holds a real match; <paramref name="limit"/> stops the scan after that many.
    /// </summary>
    public void CollectMatchLines(ReadOnlySpan<byte> text, List<int> starts, int limit)
    {
        var state = 0;
        var lineStart = 0;
        var last = -1;
        for (var i = 0; i < text.Length; i++)
        {
            state = _filter.Step(state, text[i], out var accept);
            if (accept && lineStart != last)
            {
                starts.Add(lineStart);
                last = lineStart;
                if (starts.Count >= limit)
                {
                    return;
                }
            }

            if (text[i] == (byte)'\n')
            {
                lineStart = i + 1;
            }
        }

        if (starts.Count < limit && _filter.AcceptsAtEnd(state) && lineStart != last)
        {
            starts.Add(lineStart);
        }
    }

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        // A miss is settled here, without touching the VM.
        if (!_filter.MayMatch(text))
        {
            return;
        }

        _inner.Collect(text, allowNewline, stopAtFirst, hits);
    }
}

/// <summary>
/// Deterministic byte automaton over a NFA whose anchors are position dependent. A state is
/// a set of live pcs plus the three context bits an anchor can read: whether the scan is at
/// the first byte, whether the previous byte was a newline, and whether it was a word
/// character. Its only answer is whether a match exists, which is exact for this instruction
/// subset.
/// </summary>
internal sealed class DfaTable
{
    private const int AcceptBit = 1 << 24;
    private const int StateMask = AcceptBit - 1;

    private const int AtStart = 1;
    private const int PrevNewline = 2;
    private const int PrevWord = 4;

    /// <summary><c>state &lt;&lt; 8 | byte</c> gives the next state, with <see cref="AcceptBit"/> when a match ends there.</summary>
    private readonly int[] _trans;

    /// <summary>Whether a match can end at the end of the input from that state.</summary>
    private readonly bool[] _endAccept;

    private DfaTable(int[] trans, bool[] endAccept, bool alwaysAccepts)
    {
        _trans = trans;
        _endAccept = endAccept;
        AlwaysAccepts = alwaysAccepts;
    }

    /// <summary>The filter can never reject, so wrapping would only add work.</summary>
    public bool AlwaysAccepts { get; }

    public bool MayMatch(ReadOnlySpan<byte> text)
    {
        var state = 0;
        foreach (var value in text)
        {
            var entry = _trans[(state << 8) | value];
            if ((entry & AcceptBit) != 0)
            {
                return true;
            }

            state = entry & StateMask;
        }

        return _endAccept[state];
    }

    /// <summary>
    /// One transition, reporting whether a match ends before this byte. Accept is exact:
    /// the current state reaches a Match pc once the anchor context of <paramref name="value"/> is applied.
    /// </summary>
    public int Step(int state, byte value, out bool accept)
    {
        var entry = _trans[(state << 8) | value];
        accept = (entry & AcceptBit) != 0;
        return entry & StateMask;
    }

    public bool AcceptsAtEnd(int state) => _endAccept[state];

    public static DfaTable? TryBuild(PikeProgram.Inst[] insts, int start, int maxStates)
    {
        // A match ends at the current position once a Match pc becomes live.
        // Phase 1: resolve everything at one position (epsilon steps and anchors) until
        // only Class and Match pcs remain. Anchors may chain, so this repeats at one position.
        ulong ResolveAt(ulong mask, int context, byte value, bool atEnd, out bool matched)
        {
            var result = 0UL;
            var stack = mask;
            var visited = 0UL;
            matched = false;
            var atStart = (context & AtStart) != 0;
            var previousNewline = (context & PrevNewline) != 0;
            var previousWord = (context & PrevWord) != 0;
            var currentWord = !atEnd && IsWord(value);
            while (stack != 0)
            {
                var pc = BitOperations.TrailingZeroCount(stack);
                stack &= stack - 1;
                var bit = 1UL << pc;
                if ((visited & bit) != 0)
                {
                    continue;
                }

                visited |= bit;
                ref readonly var inst = ref insts[pc];
                switch (inst.Op)
                {
                    case PikeProgram.Op.Split:
                        stack |= 1UL << inst.X;
                        stack |= 1UL << inst.Y;
                        break;
                    case PikeProgram.Op.Jump:
                    case PikeProgram.Op.Save:
                        stack |= 1UL << inst.X;
                        break;
                    case PikeProgram.Op.Class:
                        result |= bit;
                        break;
                    case PikeProgram.Op.Match:
                        matched = true;
                        break;
                    case PikeProgram.Op.Bol:
                        if (atStart || previousNewline)
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    case PikeProgram.Op.Eol:
                        if (atEnd || value == (byte)'\n')
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    case PikeProgram.Op.Bos:
                        if (atStart)
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    case PikeProgram.Op.Eos:
                        if (atEnd)
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    case PikeProgram.Op.Word:
                        if (previousWord != currentWord)
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    case PikeProgram.Op.NotWord:
                        if (previousWord == currentWord)
                        {
                            stack |= 1UL << inst.X;
                        }

                        break;
                    default:
                        // Eow is rejected before building; anything else is unknown.
                        return 0;
                }
            }

            return result;
        }

        // Phase 2: epsilon closure that stops at Class, Match, and anchor pcs, leaving the
        // anchor test to the position where its byte is known.
        ulong DeferAnchors(ulong seed)
        {
            var result = 0UL;
            var stack = seed;
            var visited = 0UL;
            while (stack != 0)
            {
                var pc = BitOperations.TrailingZeroCount(stack);
                stack &= stack - 1;
                var bit = 1UL << pc;
                if ((visited & bit) != 0)
                {
                    continue;
                }

                visited |= bit;
                ref readonly var inst = ref insts[pc];
                switch (inst.Op)
                {
                    case PikeProgram.Op.Split:
                        stack |= 1UL << inst.X;
                        stack |= 1UL << inst.Y;
                        break;
                    case PikeProgram.Op.Jump:
                    case PikeProgram.Op.Save:
                        stack |= 1UL << inst.X;
                        break;
                    default:
                        result |= bit;
                        break;
                }
            }

            return result;
        }

        var initialMask = DeferAnchors(1UL << start);
        if (initialMask == 0)
        {
            return null;
        }

        var masks = new List<ulong>(maxStates) { initialMask };
        var contexts = new List<int>(maxStates) { AtStart };
        var index = new Dictionary<(ulong Mask, int Context), int>(maxStates)
        {
            [(initialMask, AtStart)] = 0,
        };
        var trans = new List<int[]>(maxStates) { new int[256] };

        for (var state = 0; state < masks.Count; state++)
        {
            var mask = masks[state];
            var context = contexts[state];
            var row = trans[state];
            for (var value = 0; value < 256; value++)
            {
                var ready = ResolveAt(mask, context, (byte)value, atEnd: false, out var matched);

                var consumed = 0UL;
                var live = ready;
                while (live != 0)
                {
                    var pc = BitOperations.TrailingZeroCount(live);
                    live &= live - 1;
                    ref readonly var inst = ref insts[pc];
                    if (inst.Op == PikeProgram.Op.Class
                        && ByteAutomaton.MaskHas(inst.M0, inst.M1, inst.M2, inst.M3, (byte)value))
                    {
                        consumed |= 1UL << inst.X;
                    }
                }

                var nextMask = DeferAnchors(consumed);
                var nextContext = 0;
                if (value == (byte)'\n')
                {
                    nextContext |= PrevNewline;
                }

                if (IsWord((byte)value))
                {
                    nextContext |= PrevWord;
                }

                var key = (nextMask, nextContext);
                if (!index.TryGetValue(key, out var id))
                {
                    if (masks.Count >= maxStates)
                    {
                        // Too many states to be worth it: the VM runs alone.
                        return null;
                    }

                    id = masks.Count;
                    index[key] = id;
                    masks.Add(nextMask);
                    contexts.Add(nextContext);
                    trans.Add(new int[256]);
                }

                row[value] = id | (matched ? AcceptBit : 0);
            }
        }

        var endAccept = new bool[masks.Count];
        var always = true;
        for (var state = 0; state < masks.Count; state++)
        {
            _ = ResolveAt(masks[state], contexts[state], value: 0, atEnd: true, out var matched);
            endAccept[state] = matched;
        }

        for (var value = 0; value < 256; value++)
        {
            if ((trans[0][value] & AcceptBit) == 0)
            {
                always = false;
                break;
            }
        }

        always &= endAccept[0];

        var flat = new int[masks.Count * 256];
        for (var state = 0; state < masks.Count; state++)
        {
            Array.Copy(trans[state], 0, flat, state * 256, 256);
        }

        return new DfaTable(flat, endAccept, always);
    }

    private static bool IsWord(byte value) =>
        value == (byte)'_'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'a' and <= (byte)'z';
}
