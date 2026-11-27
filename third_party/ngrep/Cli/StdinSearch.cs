using Ngrep.Text;

namespace Ngrep.Cli;

/// <summary>
/// Ripgrep's split between pattern stdin and content stdin.
/// <c>-f -</c> consumes stdin for patterns. A path of <c>-</c> searches stdin.
/// With no path, stdin is searched only when it is not a terminal and <c>-f -</c> did not take it.
/// </summary>
internal static class StdinSearch
{
    public static bool ShouldSearch(BoundSearch bound)
    {
        var patternStdin = 0;
        foreach (var file in bound.PatternFiles)
        {
            if (file == "-")
            {
                patternStdin++;
            }
        }

        if (patternStdin > 1)
        {
            throw new ArgumentException("error reading -f/--file from stdin: stdin has already been consumed");
        }

        var contentDash = 0;
        if (bound.PathExplicit)
        {
            foreach (var path in bound.Paths)
            {
                if (path == "-")
                {
                    contentDash++;
                }
            }
        }

        if (contentDash > 0)
        {
            if (patternStdin > 0)
            {
                throw new ArgumentException("attempted to read patterns from stdin while also searching stdin");
            }

            // A lone "-" is the whole search. Mixed with other roots, the walk handles "-" itself.
            return contentDash == bound.Paths.Length;
        }

        // -f - already owns stdin, so a missing path stays the working directory.
        return !bound.PathExplicit && patternStdin == 0 && StandardInput.IsRedirected;
    }
}
