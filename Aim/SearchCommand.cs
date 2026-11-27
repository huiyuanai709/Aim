using System.CommandLine;
using Ngrep.Cli;

namespace Aim;

public sealed class SearchCommand : Command
{
    public SearchCommand() : base(
        "search",
        "Search file contents with ngrep, in-process. `aim search <pattern> [PATH...]` uses .ngrep/index when one is found and scans otherwise. `aim search index` builds that index. `aim search --help` lists flags.")
    {
        // Flags are ngrep's. Program.cs forwards `aim search ...` before this parser
        // sees them, so `--fixed-strings` and the rest are not rejected as unknown options.
    }

    public static bool WantsHelp(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg == "--")
                return false;
            if (arg is "--help" or "-h" or "-?" or "/?")
                return true;
        }

        return false;
    }

    public static Task<int> Invoke(string[] args)
    {
        if (WantsHelp(args))
        {
            Console.WriteLine(HelpText);
            return Task.FromResult(0);
        }

        PrepareThreads();
        return NgrepCli.Run(args);
    }

    internal static void PrepareThreads()
    {
        if (Interlocked.Exchange(ref Ready, 1) != 0)
            return;

        ThreadPool.GetMinThreads(out var workers, out var io);
        var min = Math.Max(workers, Environment.ProcessorCount * 2);
        ThreadPool.SetMinThreads(min, Math.Max(io, Environment.ProcessorCount));
    }

    private static int Ready;

    internal const string HelpText =
        """
        Search file contents with ngrep, in this process. Patterns are .NET regular expressions unless -F.

        Usage:
          aim search <pattern> [PATH...] [search flags]
          aim search search <pattern> [PATH...] [search flags]
          aim search index <path> [--index <dir>] [--quiet] [--format text|json|ndjson]
          aim search watch <path> [--index <dir>] [--format text|json|ndjson]
          aim search --files [PATH...] [filters]

        `aim search <pattern> [PATH...]` is the same search as `aim search search`.
        Each PATH is a root. No PATH searches the working directory when stdin is a terminal,
        and searches stdin when stdin is a pipe. `-` searches stdin. `-f -` reads patterns from stdin.

        An index at <root>/.ngrep/index is used when the query can use it. Search does not create one.
        `aim search index <path>` builds or updates <path>/.ngrep/index (size, mtime, then SHA-256).
        `aim search watch <path>` refreshes that index as files change.
        `--files` lists paths that would be searched and does not read file contents.

        Ignore rules match ngrep: .gitignore, .ignore, .rgignore, git info/exclude, and the global
        excludes file. .git and .ngrep are skipped. Hidden names are skipped unless --hidden.
        Symlinks are skipped unless -L / --follow. A NUL in the first 8 KiB skips a file unless
        --binary or -a / --text. Files over 32 MiB are skipped.

        Search flags:
          --index <dir>                 Index directory. Default: discover <root>/.ngrep/index
          --format text|json|ndjson|rgjson
          -F, --fixed-strings           Literal text. Mutually exclusive with -E and -P
          -E, --regex, --extended-regexp
          -P, --pcre                    Backtracking .NET regex
          -i, --ignore-case             Ordinal ignore-case
          -S, --smart-case              Ignore case only when the pattern has no uppercase letter
          --case-sensitive              Default. Wins over -i and -S
          -e, --regexp <pattern>        Repeatable. Patterns are OR
          -f, --file <path>             Patterns from a file, one per line
          -g, --glob <glob>             Include or exclude. A leading ! excludes. Repeatable
          --iglob <glob>                Case-insensitive glob
          --ignore-file <path>          Extra gitignore-format file
          -t, --type <type>             Built-in file type. Repeatable
          -T, --type-not <type>
          --type-add <spec>             name:*.ext
          --type-list                   List file types and exit
          -n, --line-number             Default on
          -N, --no-line-number
          --column                      Default on. --no-column turns it off
          --no-heading                  path:line:column:text. Default when stdout is not a terminal
          --heading                     Path heading, then line:column:text
          --vimgrep                     path:line:column:text, one row per hit
          --color auto|always|never     auto skips color when stdout is not a terminal
          -H, --with-filename           Print the path. Default for a directory
          -I, --no-filename             Omit the path
          -m, --max-count <num>         Stop each file after this many matching lines
          -A, -B, -C <num>              Context after, before, or both
          -w, --word-regexp
          -x, --line-regexp
          -v, --invert-match
          -l, --files-with-matches
          --files-without-match
          -c, --count
          -q, --quiet, --silent
          -o, --only-matching
          -r, --replace <text>          Rewrite printed matches. Does not edit files
          --files                       List files that would be searched
          --scan                        Ignore the index and read contents
          --no-ignore                   Do not apply ignore files
          --no-ignore-vcs               Do not apply .gitignore, info/exclude, or global excludes
          -u, --unrestricted            -u ignore files, -uu hidden, -uuu binary
          --hidden                      Search names that start with '.'
          --binary, -a, --text          Search files with a NUL in the first 8 KiB
          -L, --follow                  Follow symlinks
          -j, --threads <n>             File-read workers. Default: processor count
          -d, --max-depth <n>
          -s, --no-messages             Hide missing-path errors. Exit code stays 2
          --stats                       Counts on stderr
          --no-config                   Do not read NGREP_CONFIG_PATH

        NGREP_CONFIG_PATH, when set, prepends search arguments from that file.
        Exit codes: 0 when a search reports a hit, 1 when it reports none, 2 on error.
        Text output is colored only for a terminal (--color auto). Use --no-column --no-heading
        when a tool wants path:line:text.

        The x64 binary keeps SSE2, AVX2, and AVX-512 in one file and picks a scanner at runtime.
        """;
}
