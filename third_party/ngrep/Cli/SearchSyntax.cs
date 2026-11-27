using System.CommandLine;
using System.CommandLine.Parsing;

namespace Ngrep.Cli;

internal readonly record struct BoundSearch(string[] Patterns, string[] PatternFiles, string[] Paths, bool PathExplicit);

/// <summary>
/// <c>ngrep &lt;pattern&gt; [PATH...]</c> is rewritten to <c>search</c> unless the first positional
/// token is <c>index</c>, <c>search</c>, or <c>watch</c>. <c>--</c> forces the search form so a
/// pattern named <c>index</c> is not taken as a command.
/// </summary>
internal static class SearchSyntax
{
    private static readonly HashSet<string> Subcommands = new(StringComparer.Ordinal)
    {
        "index",
        "search",
        "watch",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--format",
        "--index",
        "-g",
        "--glob",
        "--iglob",
        "--ignore-file",
        "-t",
        "--type",
        "-T",
        "--type-not",
        "--type-add",
        "-A",
        "--after-context",
        "-B",
        "--before-context",
        "-C",
        "--context",
        "-m",
        "--max-count",
        "-e",
        "--regexp",
        "-j",
        "--threads",
        "-d",
        "--max-depth",
        "--maxdepth",
        "--color",
        "-f",
        "--file",
        "-r",
        "--replace",
    };

    public static string[] Rewrite(string[] args)
    {
        args = ExpandShortBundles(args);
        args = ApplyUnrestricted(args);
        // -e --no-config is a pattern. The option is known, so the parser would
        // otherwise take the token as the flag and leave -e without a value.
        args = AttachNoConfigValues(args);
        args = PreferLast(args);
        if (args.Length == 0 || HasSubcommand(args))
        {
            return args;
        }

        var rewritten = new string[args.Length + 1];
        rewritten[0] = "search";
        args.CopyTo(rewritten, 1);
        return rewritten;
    }

    public static BoundSearch Bind(ParseResult result, bool typeList, bool listFiles = false)
    {
        var patterns = new List<string>();
        var files = new List<string>();
        var paths = new List<string>();
        string? filePath = null;
        var sawPatternSource = false;
        var tookPositionalPattern = false;
        string? pending = null;

        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.Option)
            {
                var name = OptionName(token.Value);
                var inline = InlineValue(token.Value);
                if (IsPatternSource(name))
                {
                    sawPatternSource = true;
                }

                if (inline != null)
                {
                    TakePatternSource(name, inline, patterns, files);
                    pending = null;
                    continue;
                }

                pending = IsValueOption(name) ? name : null;
                continue;
            }

            if (token.Type != TokenType.Argument)
            {
                continue;
            }

            if (pending != null)
            {
                TakePatternSource(pending, token.Value, patterns, files);
                pending = null;
                continue;
            }

            // --files takes a path and no pattern. -e / -f are rejected below.
            if (listFiles)
            {
                if (token.Value.Length == 0)
                {
                    throw new ArgumentException("Path is empty.");
                }

                if (filePath != null)
                {
                    throw new ArgumentException("Too many arguments. Usage: ngrep --files [path]");
                }

                filePath = token.Value;
                continue;
            }

            if (!sawPatternSource && !tookPositionalPattern)
            {
                if (token.Value.Length == 0)
                {
                    throw new ArgumentException("Pattern is empty.");
                }

                patterns.Add(token.Value);
                tookPositionalPattern = true;
                continue;
            }

            paths.Add(token.Value);
        }

        if (listFiles)
        {
            if (patterns.Count > 0 || files.Count > 0)
            {
                throw new ArgumentException("--files does not take a pattern.");
            }

            return new BoundSearch([], [], filePath == null ? ["."] : [filePath], filePath != null);
        }

        if (patterns.Count == 0 && files.Count == 0)
        {
            if (typeList)
            {
                return new BoundSearch([], [], ["."], false);
            }

            throw new ArgumentException("Pattern is required. Usage: ngrep <pattern> [PATH...]");
        }

        return new BoundSearch(
            patterns.ToArray(),
            files.ToArray(),
            paths.Count == 0 ? ["."] : paths.ToArray(),
            paths.Count > 0);
    }

    private static void TakePatternSource(string name, string value, List<string> patterns, List<string> files)
    {
        if (IsRegexpOption(name))
        {
            if (value.Length == 0)
            {
                throw new ArgumentException("Pattern is empty.");
            }

            patterns.Add(value);
            return;
        }

        if (IsFileOption(name))
        {
            if (value.Length == 0)
            {
                throw new ArgumentException("Pattern file path is empty.");
            }

            files.Add(value);
        }
    }

    private static bool HasSubcommand(string[] args)
    {
        var sawPatternSource = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?" or "--version")
            {
                return true;
            }

            if (arg == "--")
            {
                return false;
            }

            if (arg.StartsWith('-'))
            {
                var name = OptionName(arg);
                if (IsPatternSource(name))
                {
                    sawPatternSource = true;
                }

                if (arg.Contains('='))
                {
                    continue;
                }

                if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && ValueOptions.Contains(arg[..2]))
                {
                    continue;
                }

                if (ValueOptions.Contains(arg) || ValueOptions.Contains(name))
                {
                    i++;
                }

                continue;
            }

            if (sawPatternSource)
            {
                return false;
            }

            return Subcommands.Contains(arg);
        }

        // `-e` / `-f` with no path is still the search command. The path stays implicit (`.`, or stdin when that stream is the haystack).
        if (sawPatternSource)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--type-list" or "--files")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>-u</c> / <c>--unrestricted</c> stacks like ripgrep, up to three times.
    /// One is <c>--no-ignore</c>, two also <c>--hidden</c>, three also <c>--binary</c>.
    /// The tokens are rewritten here so a repeated flag is not rejected by the parser.
    /// A fourth use is an error. Tokens after <c>--</c> are patterns, not flags.
    /// </summary>
    private static string[] ApplyUnrestricted(string[] args)
    {
        var count = 0;
        var kept = new List<string>(args.Length + 3);
        var passthrough = false;
        foreach (var arg in args)
        {
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }

            if (arg is "-u" or "--unrestricted")
            {
                count++;
                continue;
            }

            kept.Add(arg);
        }

        if (count == 0)
        {
            return kept.ToArray();
        }

        if (count > 3)
        {
            throw new ArgumentException("-u/--unrestricted can be repeated at most 3 times.");
        }

        var insertAt = kept.Count;
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i] == "--")
            {
                insertAt = i;
                break;
            }
        }

        if (count >= 1 && !ContainsFlag(kept, "--no-ignore"))
        {
            kept.Insert(insertAt++, "--no-ignore");
        }

        if (count >= 2 && !ContainsFlag(kept, "--hidden"))
        {
            kept.Insert(insertAt++, "--hidden");
        }

        if (count >= 3 && !ContainsFlag(kept, "--binary"))
        {
            kept.Insert(insertAt, "--binary");
        }

        return kept.ToArray();
    }

    private static bool ContainsFlag(List<string> args, string flag)
    {
        foreach (var arg in args)
        {
            if (arg == "--")
            {
                return false;
            }

            if (arg == flag)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValueOption(string token)
    {
        if (ValueOptions.Contains(token))
        {
            return true;
        }

        if (token.Length > 2 && token[0] == '-' && token[1] != '-' && ValueOptions.Contains(token[..2]))
        {
            return true;
        }

        return false;
    }

    private static string OptionName(string token)
    {
        var eq = token.IndexOf('=');
        if (eq >= 0)
        {
            token = token[..eq];
        }

        if (token.Length > 2 && token[0] == '-' && token[1] != '-' && ValueOptions.Contains(token[..2]))
        {
            return token[..2];
        }

        return token;
    }

    private static string? InlineValue(string token)
    {
        var eq = token.IndexOf('=');
        return eq >= 0 ? token[(eq + 1)..] : null;
    }

    private static bool IsPatternSource(string name) => IsRegexpOption(name) || IsFileOption(name);

    private static bool IsRegexpOption(string token) => token is "-e" or "--regexp";

    private static bool IsFileOption(string token) => token is "-f" or "--file";

    /// <summary>
    /// Boolean shorts may share one token (<c>-nEi</c>, <c>-uu</c>). A value short ends the cluster and
    /// keeps the rest of the token (<c>-nEepat</c> is <c>-n -E -epat</c>). A token that starts
    /// with a value short is left as an attached value (<c>-epat</c>, <c>-A2</c>).
    /// </summary>
    private static string[] ExpandShortBundles(string[] args)
    {
        var expanded = new List<string>(args.Length);
        var passthrough = false;
        foreach (var arg in args)
        {
            if (passthrough)
            {
                expanded.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                passthrough = true;
                expanded.Add(arg);
                continue;
            }

            if (!TryExpandShortBundle(arg, expanded))
            {
                expanded.Add(arg);
            }
        }

        return expanded.ToArray();
    }

    private const string BoolShorts = "iEFPqnsxvwlcoUuSN0LaHI";

    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "--format", "--index", "--help", "-h", "-?", "--version",
        "--ignore-case", "-i", "--smart-case", "-S", "--case-sensitive",
        "--regex", "-E", "--extended-regexp", "--fixed-strings", "-F", "--pcre", "-P",
        "--scan", "-g", "--glob", "--iglob", "--ignore-file", "-t", "--type", "-T", "--type-not", "--type-list", "--type-add",
        "-A", "--after-context", "-B", "--before-context", "-C", "--context",
        "-m", "--max-count",
        "-e", "--regexp", "-f", "--file", "-w", "--word-regexp", "-x", "--line-regexp",
        "-v", "--invert-match", "--no-ignore", "--no-ignore-vcs", "-j", "--threads",
        "-l", "--files-with-matches", "--files-without-match", "-c", "--count",
        "-q", "--quiet", "--silent", "-n", "--line-number", "-N", "--no-line-number",
        "--column", "--no-column", "--vimgrep", "-0", "--null", "-r", "--replace",
        "-s", "--no-messages",
        "-o", "--only-matching", "--hidden", "--color", "-U", "--multiline",
        "--multiline-dotall", "--heading", "--no-heading", "--with-filename", "-H",
        "--no-filename", "-I", "--stats",
        "--files", "--binary", "-a", "--text", "-u", "--unrestricted",
        "--no-config",
    };

    /// <summary>
    /// A config word that starts with <c>-</c> must be a flag ngrep accepts.
    /// Values and patterns are not checked here.
    /// </summary>
    internal static bool IsKnownFlag(string argument)
    {
        if (argument.Length < 2 || argument[0] != '-' || argument == "--")
        {
            return true;
        }

        var body = argument;
        var eq = argument.IndexOf('=');
        if (eq >= 0)
        {
            body = argument[..eq];
        }

        if (KnownFlags.Contains(body))
        {
            return true;
        }

        if (body.Length > 2 && body[1] != '-' && ValueShorts.Contains(body[1]))
        {
            return true;
        }

        if (body.Length > 2 && body[1] != '-' && eq < 0)
        {
            for (var i = 1; i < body.Length; i++)
            {
                if (BoolShorts.Contains(body[i]))
                {
                    continue;
                }

                // A value short ends the cluster and keeps the rest as its operand.
                return ValueShorts.Contains(body[i]);
            }

            return true;
        }

        return false;
    }

    private const string ValueShorts = "defgtTABCjrm";

    private static bool TryExpandShortBundle(string arg, List<string> destination)
    {
        if (arg.Length <= 2 || arg[0] != '-' || arg[1] == '-' || arg.Contains('='))
        {
            return false;
        }

        if (ValueShorts.Contains(arg[1]))
        {
            return false;
        }

        var parts = new List<string>();
        for (var i = 1; i < arg.Length; i++)
        {
            var ch = arg[i];
            if (BoolShorts.Contains(ch))
            {
                parts.Add("-" + ch);
                continue;
            }

            if (ValueShorts.Contains(ch))
            {
                var rest = arg[(i + 1)..];
                // "=" keeps a value that starts with '-' attached to the option.
                parts.Add(rest.Length == 0 ? "-" + ch : "-" + ch + "=" + rest);
                destination.AddRange(parts);
                return true;
            }

            return false;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        destination.AddRange(parts);
        return true;
    }

    /// <summary>
    /// <c>index</c>, <c>watch</c>, help, version, and <c>--no-config</c> do not read the config file.
    /// A bare <c>ngrep</c> with no arguments does not either.
    /// <c>--no-config</c> is detected here, before the file is opened. A token after <c>--</c>,
    /// or the value of another option, is not the flag.
    /// </summary>
    internal static bool SkipsConfig(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        if (HasNoConfig(args))
        {
            return true;
        }

        var commandAt = IndexOfCommand(args);
        if (commandAt >= 0 && args[commandAt] is "index" or "watch")
        {
            return true;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                break;
            }

            if (arg is "--help" or "-h" or "-?" or "--version")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <c>--no-config</c> is a flag on the raw command line.
    /// The scan stops at <c>--</c>. The operand of a value option is not the flag,
    /// so <c>-e --no-config</c> searches for that pattern and still reads the config file.
    /// </summary>
    private static bool HasNoConfig(string[] args)
    {
        // Match Rewrite, which expands -nEe before the parser sees it.
        args = ExpandShortBundles(args);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                return false;
            }

            if (arg == "--no-config")
            {
                return true;
            }

            if (ConsumesNext(arg))
            {
                i++;
            }
        }

        return false;
    }

    /// <summary>
    /// Joins a value option to a following <c>--no-config</c> operand (<c>-e=--no-config</c>).
    /// The config decision has already been made from the raw arguments.
    /// </summary>
    private static string[] AttachNoConfigValues(string[] args)
    {
        var attached = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (ConsumesNext(args[i]) && i + 1 < args.Length && args[i + 1] == "--no-config")
            {
                attached = true;
                break;
            }
        }

        if (!attached)
        {
            return args;
        }

        var rewritten = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (passthrough)
            {
                rewritten.Add(args[i]);
                continue;
            }

            if (args[i] == "--")
            {
                passthrough = true;
                rewritten.Add(args[i]);
                continue;
            }

            if (ConsumesNext(args[i]) && i + 1 < args.Length && args[i + 1] == "--no-config")
            {
                rewritten.Add(args[i] + "=--no-config");
                i++;
                continue;
            }

            rewritten.Add(args[i]);
        }

        return rewritten.ToArray();
    }

    private static bool ConsumesNext(string arg)
    {
        if (arg.Length < 2 || arg[0] != '-' || arg == "--" || arg.Contains('='))
        {
            return false;
        }

        // -j1 and -g*.cs already include the operand.
        if (arg.Length > 2 && arg[1] != '-' && ValueOptions.Contains(arg[..2]))
        {
            return false;
        }

        var name = OptionName(arg);
        return ValueOptions.Contains(arg) || ValueOptions.Contains(name);
    }

    /// <summary>
    /// Places config arguments before the user's search arguments so a later CLI flag wins.
    /// An explicit <c>search</c> token stays where it is; the config words follow it.
    /// </summary>
    internal static string[] PrependConfig(string[] args, string[] configArgs)
    {
        if (configArgs.Length == 0)
        {
            return args;
        }

        var commandAt = IndexOfCommand(args);
        if (commandAt >= 0 && args[commandAt] == "search")
        {
            var merged = new string[args.Length + configArgs.Length];
            var head = commandAt + 1;
            Array.Copy(args, merged, head);
            configArgs.CopyTo(merged, head);
            Array.Copy(args, head, merged, head + configArgs.Length, args.Length - head);
            return merged;
        }

        var prepended = new string[args.Length + configArgs.Length];
        configArgs.CopyTo(prepended, 0);
        args.CopyTo(prepended, configArgs.Length);
        return prepended;
    }

    private static int IndexOfCommand(string[] args)
    {
        var sawPatternSource = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                return -1;
            }

            if (arg is "--help" or "-h" or "-?" or "--version")
            {
                return -1;
            }

            if (arg.StartsWith('-'))
            {
                var name = OptionName(arg);
                if (IsPatternSource(name))
                {
                    sawPatternSource = true;
                }

                if (arg.Contains('='))
                {
                    continue;
                }

                if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && ValueOptions.Contains(arg[..2]))
                {
                    continue;
                }

                if (ValueOptions.Contains(arg) || ValueOptions.Contains(name))
                {
                    i++;
                }

                continue;
            }

            if (sawPatternSource)
            {
                return -1;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// The last occurrence of a single-value option wins, and a repeated boolean is kept once.
    /// Distinct case flags stay in order so the last of <c>-i</c>, <c>-S</c>, and <c>--case-sensitive</c> wins.
    /// </summary>
    private static string[] PreferLast(string[] args)
    {
        var spans = new List<ArgSpan>();
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (passthrough)
            {
                continue;
            }

            var arg = args[i];
            if (arg == "--")
            {
                passthrough = true;
                continue;
            }

            if (BoolKey(arg) is { } boolKey)
            {
                spans.Add(new ArgSpan(i, i + 1, boolKey));
                continue;
            }

            if (!TrySingleValue(arg, out var key, out var attached))
            {
                continue;
            }

            var end = i + 1;
            if (!attached && end < args.Length && !args[end].StartsWith('-'))
            {
                end++;
            }

            spans.Add(new ArgSpan(i, end, key));
        }

        if (spans.Count < 2)
        {
            return args;
        }

        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var s = 0; s < spans.Count; s++)
        {
            last[spans[s].Key] = s;
        }

        var drop = new bool[args.Length];
        var dropped = false;
        for (var s = 0; s < spans.Count; s++)
        {
            if (last[spans[s].Key] == s)
            {
                continue;
            }

            for (var i = spans[s].Start; i < spans[s].End; i++)
            {
                drop[i] = true;
                dropped = true;
            }
        }

        if (!dropped)
        {
            return args;
        }

        var kept = new List<string>(args.Length - 1);
        for (var i = 0; i < args.Length; i++)
        {
            if (!drop[i])
            {
                kept.Add(args[i]);
            }
        }

        return kept.ToArray();
    }

    private static string? BoolKey(string arg)
    {
        var name = OptionName(arg);
        return name switch
        {
            "-i" or "--ignore-case" => "ignore-case",
            "-S" or "--smart-case" => "smart-case",
            "--case-sensitive" => "case-sensitive",
            "-E" or "--regex" or "--extended-regexp" => "regex",
            "-F" or "--fixed-strings" => "fixed-strings",
            "-P" or "--pcre" => "pcre",
            "--scan" => "scan",
            "--type-list" => "type-list",
            "-w" or "--word-regexp" => "word-regexp",
            "-x" or "--line-regexp" => "line-regexp",
            "-v" or "--invert-match" => "invert-match",
            "--no-ignore" => "no-ignore",
            "--no-ignore-vcs" => "no-ignore-vcs",
            "-l" or "--files-with-matches" => "files-with-matches",
            "--files-without-match" => "files-without-match",
            "-c" or "--count" => "count",
            "-q" or "--quiet" or "--silent" => "quiet",
            "-n" or "--line-number" => "line-number",
            "-s" or "--no-messages" => "no-messages",
            "-o" or "--only-matching" => "only-matching",
            "--hidden" => "hidden",
            "--binary" => "binary",
            "-a" or "--text" => "text",
            "--files" => "files",
            "-U" or "--multiline" => "multiline",
            "--multiline-dotall" => "multiline-dotall",
            "--heading" => "heading",
            "--no-heading" => "no-heading",
            "-H" or "--with-filename" or "-I" or "--no-filename" => "filename",
            "--stats" => "stats",
            "--no-config" => "no-config",
            _ => null,
        };
    }

    private static bool TrySingleValue(string arg, out string key, out bool attached)
    {
        attached = false;
        key = "";
        if (arg.Length < 2 || arg[0] != '-' || arg == "--")
        {
            return false;
        }

        var body = arg;
        var eq = arg.IndexOf('=');
        if (eq >= 0)
        {
            attached = true;
            body = arg[..eq];
        }

        key = body switch
        {
            "--format" => "format",
            "--index" => "index",
            "--color" => "color",
            "-A" or "--after-context" => "after",
            "-B" or "--before-context" => "before",
            "-C" or "--context" => "context",
            "-m" or "--max-count" => "max-count",
            "-j" or "--threads" => "threads",
            _ => "",
        };
        if (key.Length > 0)
        {
            return true;
        }

        if (body.Length > 2 && body[1] != '-')
        {
            key = body[..2] switch
            {
                "-A" => "after",
                "-B" => "before",
                "-C" => "context",
                "-m" => "max-count",
                "-j" => "threads",
                _ => "",
            };
            if (key.Length > 0)
            {
                attached = true;
                return true;
            }
        }

        return false;
    }

    private readonly record struct ArgSpan(int Start, int End, string Key);
}
