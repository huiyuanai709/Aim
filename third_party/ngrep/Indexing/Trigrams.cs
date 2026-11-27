using System.Collections.Concurrent;

namespace Ngrep.Indexing;

internal static class Trigrams
{
    public const int Length = 3;

    public static HashSet<string> Extract(string text)
    {
        var folded = text.ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (folded.Length < Length)
        {
            return set;
        }

        // One string per distinct window. The old loop allocated a string at every offset.
        var seen = new HashSet<ulong>();
        for (var i = 0; i <= folded.Length - Length; i++)
        {
            if (!seen.Add(Pack(folded[i], folded[i + 1], folded[i + 2])))
            {
                continue;
            }

            set.Add(folded.Substring(i, Length));
        }

        return set;
    }

    /// <summary>
    /// Appends the distinct folded trigrams of <paramref name="folded"/> onto <paramref name="dest"/>.
    /// <paramref name="seen"/> and the intern table let a worker reuse the same buffers across files.
    /// </summary>
    public static void Collect(string folded, HashSet<ulong> seen, List<string> dest, ConcurrentDictionary<ulong, string> intern)
    {
        seen.Clear();
        if (folded.Length < Length)
        {
            return;
        }

        for (var i = 0; i <= folded.Length - Length; i++)
        {
            var key = Pack(folded[i], folded[i + 1], folded[i + 2]);
            if (!seen.Add(key))
            {
                continue;
            }

            var created = folded.Substring(i, Length);
            dest.Add(intern.GetOrAdd(key, created));
        }
    }

    public static List<string> ForQuery(string pattern)
    {
        var folded = pattern.ToLowerInvariant();
        var list = new List<string>(Math.Max(0, folded.Length - 2));
        for (var i = 0; i <= folded.Length - Length; i++)
        {
            list.Add(folded.Substring(i, Length));
        }

        return list;
    }

    private static ulong Pack(char a, char b, char c) =>
        ((ulong)a << 32) | ((ulong)b << 16) | c;
}
