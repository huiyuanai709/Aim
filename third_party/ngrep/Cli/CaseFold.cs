using System.CommandLine;
using System.CommandLine.Parsing;

namespace Ngrep.Cli;

internal enum CaseMatch
{
    Sensitive,
    Insensitive,
    Smart,
}

/// <summary>
/// Case mode for one search. <see cref="CaseMatch.Smart"/> folds a pattern only when
/// the pattern text has no uppercase letter. The last of <c>-i</c>, <c>-S</c>, and
/// <c>--case-sensitive</c> on the parsed command wins.
/// </summary>
internal static class CaseFold
{
    internal readonly record struct Decision(bool IgnoreCase, bool[]? PerPattern);

    internal static CaseMatch Resolve(ParseResult result)
    {
        var mode = CaseMatch.Sensitive;
        foreach (var token in result.Tokens)
        {
            if (token.Type != TokenType.Option)
            {
                continue;
            }

            switch (FlagName(token.Value))
            {
                case "-i":
                case "--ignore-case":
                    mode = CaseMatch.Insensitive;
                    break;
                case "-S":
                case "--smart-case":
                    mode = CaseMatch.Smart;
                    break;
                case "--case-sensitive":
                    mode = CaseMatch.Sensitive;
                    break;
            }
        }

        return mode;
    }

    /// <summary>
    /// Uniform folding uses <see cref="Decision.IgnoreCase"/> alone.
    /// Mixed smart-case sets <see cref="Decision.PerPattern"/> and leaves the uniform flag off.
    /// </summary>
    internal static Decision Decide(CaseMatch mode, IReadOnlyList<string> patterns)
    {
        var folds = new bool[patterns.Count];
        var any = false;
        var all = patterns.Count > 0;
        for (var i = 0; i < patterns.Count; i++)
        {
            var fold = Applies(mode, patterns[i]);
            folds[i] = fold;
            any |= fold;
            all &= fold;
        }

        if (any && !all)
        {
            return new Decision(false, folds);
        }

        return new Decision(any && all, null);
    }

    internal static bool Applies(CaseMatch mode, string pattern) => mode switch
    {
        CaseMatch.Insensitive => true,
        CaseMatch.Smart => !HasUppercase(pattern),
        _ => false,
    };

    internal static bool HasUppercase(string pattern)
    {
        foreach (var ch in pattern)
        {
            if (char.IsUpper(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static string FlagName(string token)
    {
        var eq = token.IndexOf('=');
        return eq >= 0 ? token[..eq] : token;
    }
}
