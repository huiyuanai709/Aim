using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Output;
using Ngrep.Search;
using Ngrep.Text;
using Ngrep.Watch;

namespace Ngrep.Cli;

internal static class NgrepCli
{
    /// <summary>
    /// First Ctrl+C cancels <paramref name="cts"/> and returns true. A later one returns
    /// false; the caller force-exits. In-flight <c>RandomAccess.ReadAsync</c> calls do not
    /// take the token (Windows overlapped dispose race), so they stop at the next chunk.
    /// </summary>
    internal static bool RequestSearchCancel(ref int interrupts, CancellationTokenSource cts)
    {
        if (Interlocked.Increment(ref interrupts) > 1)
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    public static async Task<int> Run(string[] args)
    {
        try
        {
            args = ConfigFile.Apply(args, out var config);
            var root = Build();
            var parsed = root.Parse(SearchSyntax.Rewrite(args));
            if (parsed.Errors.Count > 0)
            {
                foreach (var error in parsed.Errors)
                {
                    if (config is { } loaded && ConfigFile.Blames(error.Message, loaded.Arguments))
                    {
                        Console.Error.WriteLine($"NGREP_CONFIG_PATH: {loaded.Path}: {error.Message}");
                    }
                    else
                    {
                        Console.Error.WriteLine(error.Message);
                    }
                }

                return ExitCodes.Error;
            }

            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }
        finally
        {
            Output.Stdout.Flush();
        }
    }

    private static RootCommand Build()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json, ndjson, or rgjson. rgjson is a search-only event stream.",
            DefaultValueFactory = _ => "text",
            Recursive = true,
        };
        format.AcceptOnlyFromAmong("text", "json", "ndjson", "rgjson");

        var index = new Option<string>("--index")
        {
            Description = "Index directory. Defaults to <root>/.ngrep/index. Search walks parents to find .ngrep/index when omitted.",
            Recursive = true,
        };
        var noConfig = new Option<bool>("--no-config")
        {
            Description = "Never read configuration files. When this flag is present, NGREP_CONFIG_PATH is not opened and flags in that file are not applied. Recognized before the file is loaded, wherever it appears before --. The value of another option is not this flag. A --no-config line inside the config file does not skip that file. RIPGREP_CONFIG_PATH is not read either way.",
            Recursive = true,
        };

        var root = new RootCommand(
            "Search a tree, or stdin when the path is '-' or omitted and stdin is not a terminal. " +
            "`ngrep <pattern> [PATH...]` is the same as `ngrep search`. " +
            "Uses an index when one is found; otherwise scans file contents. " +
            "Each PATH is a separate root. No PATH searches `.` when stdin is a terminal. " +
            "`-f -` reads patterns from stdin and does not search that stream. " +
            "NGREP_CONFIG_PATH, when set, prepends search arguments from that file (one argument per line). " +
            "--no-config skips that file.");
        root.Options.Add(format);
        root.Options.Add(index);
        root.Options.Add(noConfig);

        var indexPath = new Argument<string>("path")
        {
            Description = "Directory to index.",
        };
        var indexQuiet = new Option<bool>("--quiet", "-q")
        {
            Description = "Silence index progress on stderr. The stdout summary is unchanged. Search -q is a different flag and suppresses match output.",
        };
        var indexCommand = new Command("index", "Walk a tree and build or update the inverted index.");
        indexCommand.Arguments.Add(indexPath);
        indexCommand.Options.Add(indexQuiet);
        indexCommand.SetAction(result =>
        {
            var path = result.GetRequiredValue(indexPath);
            var built = Indexer.Build(path, result.GetValue(index), showProgress: !result.GetValue(indexQuiet));
            MachineOutput.WriteIndex(built, OutputFormats.Parse(result.GetValue(format)));
            return ExitCodes.Success;
        });

        var positionals = new Argument<string[]>("pattern")
        {
            Description = "Search pattern and optional paths: <pattern> [PATH...]. '-' reads file contents from stdin. With no path, stdin is searched when it is not a terminal; a terminal still searches `.`. -f - reads patterns from stdin instead, and a path of '-' is then an error. With --files, the pattern is omitted and the positional is the path. With -e or -f, every -e value is a pattern and every -f value is a pattern file (OR). A positional written before the first -e or -f is also a pattern; any later positional is a path. No path searches `.` when stdin is a terminal.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var ignoreCase = new Option<bool>("--ignore-case", "-i")
        {
            Description = "Case-insensitive match (ordinal, culture-invariant). The last of -i/--ignore-case, -S/--smart-case, and --case-sensitive wins.",
        };
        var smartCase = new Option<bool>("--smart-case", "-S")
        {
            Description = "Case-insensitive when the pattern has no uppercase letter; otherwise case-sensitive. Decided per pattern. An uppercase letter in a regex escape (\\W) counts. The last of -i, -S, and --case-sensitive wins.",
        };
        var caseSensitive = new Option<bool>("--case-sensitive")
        {
            Description = "Case-sensitive match. This is the default. Overrides -i and -S, including flags from NGREP_CONFIG_PATH. -s remains --no-messages.",
        };
        var regex = new Option<bool>("--regex", "-E", "--extended-regexp")
        {
            Description = "Treat patterns as .NET regular expressions (the default). Not POSIX BRE or ERE, and not ripgrep's -E encoding flag. -E and --extended-regexp select this default explicitly. The engine is non-backtracking and linear. Required literals are trigram-filtered when extraction is safe.",
        };
        var fixedStrings = new Option<bool>("--fixed-strings", "-F")
        {
            Description = "Treat patterns as literal text. Mutually exclusive with -E/--regex and -P/--pcre (exit 2).",
        };
        var pcre = new Option<bool>("--pcre", "-P")
        {
            Description = "Backtracking .NET regex interpreter. Supports backreferences, lookaround, and atomic groups. The default engine is non-backtracking and linear. Mutually exclusive with -F.",
        };
        var scan = new Option<bool>("--scan")
        {
            Description = "Ignore any on-disk index and scan file contents.",
        };
        var glob = new Option<string[]>("-g", "--glob")
        {
            Description = "Include or exclude files. Repeatable. A leading ! excludes (like ripgrep). The last matching glob wins, including a later --iglob. Globs filter after ignore files and do not re-include ignored files.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var iglob = new Option<string[]>("--iglob")
        {
            Description = "Case-insensitive --glob (ordinal). Repeatable. A leading ! excludes. Mixed with -g, the last matching glob wins. Does not re-include ignored or hidden files.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var ignoreFile = new Option<string[]>("--ignore-file")
        {
            Description = "Extra gitignore-format file. Repeatable. Patterns match paths relative to the current directory. Lowest precedence: .rgignore, .ignore, .gitignore, info/exclude, and the global excludes file all override it. Later --ignore-file flags override earlier ones. --no-ignore, --no-ignore-vcs, and -u do not disable it. A missing file exits 2.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var type = new Option<string[]>("-t", "--type")
        {
            Description = "Only files of this type. Repeatable; types are OR-ed. See --type-list.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var typeNot = new Option<string[]>("-T", "--type-not")
        {
            Description = "Exclude files of this type. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var typeList = new Option<bool>("--type-list")
        {
            Description = "List built-in file types and exit 0.",
        };
        var typeAdd = new Option<string[]>("--type-add")
        {
            Description = "Add globs to a type: name:*.ext[,*.ext] or name:include:*.ext. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var after = new Option<int>("-A", "--after-context")
        {
            Description = "Lines of context to print after each match.",
        };
        var before = new Option<int>("-B", "--before-context")
        {
            Description = "Lines of context to print before each match.",
        };
        var context = new Option<int>("-C", "--context")
        {
            Description = "Lines of context before and after each match. -A and -B override that side.",
        };
        var maxCount = new Option<int>("-m", "--max-count")
        {
            Description = "Stop each file after NUM matching lines and keep searching the others. A line with several hits counts once. 0 searches nothing. The last -m/--max-count wins.",
        };
        var regexp = new Option<string[]>("-e", "--regexp")
        {
            Description = "Pattern to search for. Repeatable. Matches if any pattern matches (OR).",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var patternFile = new Option<string[]>("-f", "--file")
        {
            Description = "Read patterns from a file, one per line. An empty line matches every line. A line starting with # is a pattern, not a comment. '-' reads patterns from stdin (not file contents) and searches the working directory, or the path you passed. A second '-' or a search path of '-' exits 2 because stdin is already in use. Repeatable, OR-ed with -e and a positional pattern.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var word = new Option<bool>("-w", "--word-regexp")
        {
            Description = "Only match when both ends are word boundaries (Unicode letter/digit or underscore).",
        };
        var lineRegexp = new Option<bool>("--line-regexp", "-x")
        {
            Description = "Match only when the entire line matches. A literal must equal the line. A regex is tested against each line, with a trailing CR removed, anchored at both ends. .NET regex, not POSIX ERE.",
        };
        var invert = new Option<bool>("-v", "--invert-match")
        {
            Description = "Print lines that do not match. Does not trigram-filter; reads candidates in parallel and stops each matching line at the first hit.",
        };
        var noIgnore = new Option<bool>("--no-ignore")
        {
            Description = "Do not respect ignore files (.gitignore, .ignore, .rgignore, git info/exclude, or the global excludes file). --ignore-file still applies. Globs still filter. .git and .ngrep stay skipped. Scans, because the index omits ignored files.",
        };
        var noIgnoreVcs = new Option<bool>("--no-ignore-vcs")
        {
            Description = "Do not respect version-control ignore rules (.gitignore, git info/exclude, and the global excludes file). .ignore, .rgignore, and --ignore-file still apply. Scans instead of using the index.",
        };
        var unrestricted = new Option<bool>("--unrestricted", "-u")
        {
            Description = "Reduce filtering, like ripgrep. Repeat up to three times (-uu, -uuu, or separate -u flags). -u is --no-ignore (every ignore file, including .ignore and .rgignore), -uu also passes --hidden, and -uuu also passes --binary. A fourth -u exits 2. .git, .ngrep, the index directory, and symlinks stay skipped.",
        };
        var binary = new Option<bool>("--binary")
        {
            Description = "Search files with a NUL byte in the first 8 KiB and print matching lines. Those files are skipped otherwise. Ripgrep's --binary prints \"binary file matches\" instead of the line; this flag prints the line. -a/--text is the same search. Forces a content scan, because the index omits binary files. Files over 32 MiB stay skipped.",
        };
        var text = new Option<bool>("--text", "-a")
        {
            Description = "Search binary files as if they were text and print matching lines, like ripgrep -a/--text. A NUL in the first 8 KiB does not skip the file. Stdin is included. --binary is the same search. Forces a content scan. Files over 32 MiB stay skipped.",
        };
        var threads = new Option<int>("-j", "--threads")
        {
            Description = "Workers for file reads. Default is the processor count, or NGREP_THREADS. NETGREP_THREADS is used only when NGREP_THREADS is unset.",
        };
        var listFiles = new Option<bool>("--files")
        {
            Description = "List files that would be searched, one relative path per line, and do not search. No pattern. Respects ignore files, hidden names, globs, and types. Exit 0 if any path is listed, 1 if none, and 2 on error. Does not read file contents, so binary files and files over 32 MiB are listed. Does not use the index. --format is ignored. -0 / --null prints each path followed by NUL. -q prints nothing and keeps that exit code.",
        };
        var filesWith = new Option<bool>("--files-with-matches", "-l")
        {
            Description = "Print only the paths of files with at least one match.",
        };
        var filesWithout = new Option<bool>("--files-without-match")
        {
            Description = "Print only the paths of searched files with no matches. With -v, a match is a line that failed the pattern.",
        };
        var count = new Option<bool>("--count", "-c")
        {
            Description = "Print the number of matching lines per file (path:count). With -v, count lines that do not match. A line with several hits counts once. Zero counts are omitted.",
        };
        var quiet = new Option<bool>("--quiet", "-q", "--silent")
        {
            Description = "Print no match report on stdout. Exit 0 if any line matches, 1 if none, and 2 on error. Mutually exclusive with -c, -l, and --files-without-match. Does not hide the stderr scan note.",
        };
        var lineNumber = new Option<bool>("--line-number", "-n")
        {
            Description = "Print 1-based line numbers in text output. This is the default. -N / --no-line-number turns them off; the last of the two wins.",
        };
        var noLineNumber = new Option<bool>("--no-line-number", "-N")
        {
            Description = "Omit line numbers from text output. The default is to print them. -n turns them back on; the last flag wins. json, ndjson, and rgjson ignore this flag.",
        };
        var column = new Option<bool>("--column")
        {
            Description = "Print 1-based columns in text output. This is the default, unlike ripgrep. --no-column turns them off; the last flag wins.",
        };
        var noColumn = new Option<bool>("--no-column")
        {
            Description = "Omit columns from text output. The default is to print them. --column turns them back on; the last flag wins. json, ndjson, and rgjson ignore this flag.",
        };
        var vimgrep = new Option<bool>("--vimgrep")
        {
            Description = "Print each text match as path:line:column:text. Forces line numbers, columns, and the path, and disables headings. Overrides -N, --no-column, and --heading. -I still drops the path.",
        };
        var nullPaths = new Option<bool>("--null", "-0")
        {
            Description = "Follow each printed path with NUL instead of the usual newline or separator. Applies to match lines, context, headings, -c, -l, --files-without-match, and --files. json, ndjson, and rgjson ignore this flag. --files still honors it.",
        };
        var replace = new Option<string>("--replace", "-r")
        {
            Description = "Replace matches in text output. Does not modify files. The default regex, -E, and -P expand $0, $&, $N, ${N}, $name, and ${name}; $$ is a literal $. A missing group is empty. -F inserts the text literally. -S folds the same patterns the search folds. json, ndjson, and rgjson ignore this flag.",
        };
        var noMessages = new Option<bool>("--no-messages", "-s")
        {
            Description = "Suppress stderr for a missing search path and for an explicitly named unreadable file. The exit code is still 2. Does not hide usage errors, bad regexes, or missing pattern files. Files skipped while walking a directory stay silent.",
        };
        var onlyMatching = new Option<bool>("--only-matching", "-o")
        {
            Description = "Text output prints the matched text instead of the whole line. JSON and NDJSON keep path, line, column, text, and lineText; text is already the match.",
        };
        var hidden = new Option<bool>("--hidden")
        {
            Description = "Search hidden files and directories. A name starting with '.' is hidden. On Windows and macOS the hidden attribute counts too. .git, .ngrep, and the index directory stay skipped. Symlinks stay skipped unless -L/--follow.",
        };
        var follow = new Option<bool>("--follow", "-L")
        {
            Description = "Follow symbolic links to files and directories. Off by default. A directory link whose target is already an ancestor is skipped, so a cycle stops. .git, .ngrep, and the index directory stay skipped. Scans, because the index does not follow links.",
        };
        var color = new Option<string>("--color")
        {
            Description = "When to color text output: auto, always, or never. auto colors a terminal unless NO_COLOR is set or TERM=dumb. JSON, NDJSON, and rgjson are never colored.",
            DefaultValueFactory = _ => "auto",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var multiline = new Option<bool>("--multiline", "-U")
        {
            Description = "Let a regex match span lines. ^ and $ are line anchors either way. '.' does not match a newline unless --multiline-dotall. The default engine is non-backtracking. -P selects the backtracking interpreter. No RegexOptions.Compiled.",
        };
        var multilineDotAll = new Option<bool>("--multiline-dotall")
        {
            Description = "Let '.' match a newline. Implies --multiline.",
        };
        var heading = new Option<bool>("--heading")
        {
            Description = "Print a path heading before each file's text matches, and omit the path from match lines. Applies when the path is shown. A single file or stdin also needs -H. --no-heading wins when both are set.",
        };
        var noHeading = new Option<bool>("--no-heading")
        {
            Description = "Prefix each text match with the path instead of a heading. Overrides --heading. Default when stdout is not a terminal or TERM=dumb. Does not add a path for one file, stdin, or -I.",
        };
        var withFilename = new Option<bool>("--with-filename", "-H")
        {
            Description = "Print the file path with each text match. Default when more than one path is searched or the path is a directory. With --heading the path is a heading; otherwise it prefixes each line. --vimgrep implies this unless -I is set. The last of -H and -I wins. -c uses the same rule. json, ndjson, and rgjson ignore this flag.",
        };
        var noFilename = new Option<bool>("--no-filename", "-I")
        {
            Description = "Never print the file path with text matches, context, or -c. Default when searching one file or stdin. Lines are line:column:text. Overrides -H and the path --vimgrep would add. -l, --files-without-match, and --files still print paths. json, ndjson, and rgjson ignore this flag.",
        };
        var stats = new Option<bool>("--stats")
        {
            Description = "After the search, print files searched, files matched, lines matched, matches, and elapsed milliseconds on stderr. Stdout is unchanged. Collects every hit so -l line counts are complete.",
        };
        var maxDepth = new Option<int>("-d", "--max-depth", "--maxdepth")
        {
            Description = "Limit directory walk depth, counted like ripgrep. 0 is the given path and does not list a directory. 1 is its immediate children. Ignore rules are unchanged.",
        };

        var searchCommand = new Command(
            "search",
            "Query the index, or scan contents when no index covers the path. Patterns are .NET regular expressions unless -F. " +
            "Each PATH is a root. A path of '-' searches stdin. With no path, a non-terminal stdin is searched instead of the working directory. -f - still means patterns from stdin. " +
            "NGREP_CONFIG_PATH prepends search arguments from that file. --no-config skips that file.");
        searchCommand.Arguments.Add(positionals);
        searchCommand.Options.Add(ignoreCase);
        searchCommand.Options.Add(smartCase);
        searchCommand.Options.Add(caseSensitive);
        searchCommand.Options.Add(regex);
        searchCommand.Options.Add(pcre);
        searchCommand.Options.Add(fixedStrings);
        searchCommand.Options.Add(scan);
        searchCommand.Options.Add(glob);
        searchCommand.Options.Add(iglob);
        searchCommand.Options.Add(ignoreFile);
        searchCommand.Options.Add(type);
        searchCommand.Options.Add(typeNot);
        searchCommand.Options.Add(typeList);
        searchCommand.Options.Add(typeAdd);
        searchCommand.Options.Add(after);
        searchCommand.Options.Add(before);
        searchCommand.Options.Add(context);
        searchCommand.Options.Add(maxCount);
        searchCommand.Options.Add(regexp);
        searchCommand.Options.Add(patternFile);
        searchCommand.Options.Add(word);
        searchCommand.Options.Add(lineRegexp);
        searchCommand.Options.Add(invert);
        searchCommand.Options.Add(noIgnore);
        searchCommand.Options.Add(noIgnoreVcs);
        searchCommand.Options.Add(unrestricted);
        searchCommand.Options.Add(binary);
        searchCommand.Options.Add(text);
        searchCommand.Options.Add(threads);
        searchCommand.Options.Add(listFiles);
        searchCommand.Options.Add(filesWith);
        searchCommand.Options.Add(filesWithout);
        searchCommand.Options.Add(count);
        searchCommand.Options.Add(quiet);
        searchCommand.Options.Add(lineNumber);
        searchCommand.Options.Add(noLineNumber);
        searchCommand.Options.Add(column);
        searchCommand.Options.Add(noColumn);
        searchCommand.Options.Add(vimgrep);
        searchCommand.Options.Add(nullPaths);
        searchCommand.Options.Add(replace);
        searchCommand.Options.Add(noMessages);
        searchCommand.Options.Add(onlyMatching);
        searchCommand.Options.Add(hidden);
        searchCommand.Options.Add(follow);
        searchCommand.Options.Add(color);
        searchCommand.Options.Add(multiline);
        searchCommand.Options.Add(multilineDotAll);
        searchCommand.Options.Add(heading);
        searchCommand.Options.Add(noHeading);
        searchCommand.Options.Add(withFilename);
        searchCommand.Options.Add(noFilename);
        searchCommand.Options.Add(stats);
        searchCommand.Options.Add(maxDepth);
        searchCommand.SetAction(async (result, cancellationToken) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var interrupts = 0;
            void Cancel(object? _, ConsoleCancelEventArgs e)
            {
                // Keep the process alive so the search can observe the token. A second
                // Ctrl+C exits immediately: an in-flight read cannot be CancelIoEx'd.
                e.Cancel = true;
                if (!RequestSearchCancel(ref interrupts, cts))
                {
                    Output.Stdout.Flush();
                    Environment.Exit(ExitCodes.Error);
                }
            }

            var hooked = false;
            try
            {
                Console.CancelKeyPress += Cancel;
                hooked = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }

            try
            {
                return await ExecuteSearch(
                    result,
                    format,
                    index,
                    regex,
                    pcre,
                    scan,
                    glob,
                    iglob,
                    ignoreFile,
                    type,
                    typeNot,
                    typeList,
                    typeAdd,
                    after,
                    before,
                    context,
                    word,
                    invert,
                    noIgnore,
                    noIgnoreVcs,
                    listFiles,
                    binary,
                    text,
                    threads,
                    filesWith,
                    filesWithout,
                    count,
                    quiet,
                    lineRegexp,
                    fixedStrings,
                    noMessages,
                    onlyMatching,
                    lineNumber,
                    noLineNumber,
                    column,
                    noColumn,
                    vimgrep,
                    nullPaths,
                    replace,
                    hidden,
                    follow,
                    color,
                    multiline,
                    multilineDotAll,
                    heading,
                    noHeading,
                    withFilename,
                    noFilename,
                    stats,
                    maxDepth,
                    maxCount,
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("search canceled");
                return ExitCodes.Error;
            }
            finally
            {
                if (hooked)
                {
                    Console.CancelKeyPress -= Cancel;
                }
            }
        });

        var watchPath = new Argument<string>("path")
        {
            Description = "Directory to watch. Changes trigger an incremental reindex.",
        };
        var watchCommand = new Command("watch", "Watch a tree and incrementally refresh the index.");
        watchCommand.Arguments.Add(watchPath);
        watchCommand.SetAction(result =>
        {
            using var cts = new CancellationTokenSource();
            void Cancel(object? _, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
            }

            Console.CancelKeyPress += Cancel;
            try
            {
                return WatchCommand.Run(
                    result.GetRequiredValue(watchPath),
                    result.GetValue(index),
                    OutputFormats.Parse(result.GetValue(format)),
                    cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= Cancel;
            }
        });

        root.Subcommands.Add(indexCommand);
        root.Subcommands.Add(searchCommand);
        root.Subcommands.Add(watchCommand);
        return root;
    }

    private static async Task<int> ExecuteSearch(
        System.CommandLine.ParseResult result,
        Option<string> format,
        Option<string> index,
        Option<bool> regex,
        Option<bool> pcre,
        Option<bool> scan,
        Option<string[]> glob,
        Option<string[]> iglob,
        Option<string[]> ignoreFile,
        Option<string[]> type,
        Option<string[]> typeNot,
        Option<bool> typeList,
        Option<string[]> typeAdd,
        Option<int> after,
        Option<int> before,
        Option<int> context,
        Option<bool> word,
        Option<bool> invert,
        Option<bool> noIgnore,
        Option<bool> noIgnoreVcs,
        Option<bool> listFiles,
        Option<bool> binary,
        Option<bool> text,
        Option<int> threads,
        Option<bool> filesWith,
        Option<bool> filesWithout,
        Option<bool> count,
        Option<bool> quiet,
        Option<bool> lineRegexp,
        Option<bool> fixedStrings,
        Option<bool> noMessages,
        Option<bool> onlyMatching,
        Option<bool> lineNumber,
        Option<bool> noLineNumber,
        Option<bool> column,
        Option<bool> noColumn,
        Option<bool> vimgrep,
        Option<bool> nullPaths,
        Option<string> replace,
        Option<bool> hidden,
        Option<bool> follow,
        Option<string> color,
        Option<bool> multiline,
        Option<bool> multilineDotAll,
        Option<bool> heading,
        Option<bool> noHeading,
        Option<bool> withFilename,
        Option<bool> noFilename,
        Option<bool> stats,
        Option<int> maxDepth,
        Option<int> maxCount,
        CancellationToken cancellationToken)
    {
        var showTypes = result.GetValue(typeList);
        var adds = Values(result, typeAdd);
        if (showTypes)
        {
            foreach (var line in FileTypes.Create(adds).ListLines())
            {
                Console.WriteLine(line);
            }

            return ExitCodes.Success;
        }

        if (result.GetValue(listFiles))
        {
            return ListFiles(result, glob, iglob, ignoreFile, type, typeNot, adds, noIgnore, noIgnoreVcs, hidden, quiet, noMessages, filesWith, filesWithout, count, nullPaths, cancellationToken);
        }

        var bound = SearchSyntax.Bind(result, typeList: false);
        if (result.GetValue(fixedStrings) && (result.GetValue(regex) || result.GetValue(pcre)))
        {
            throw new ArgumentException("-E/--regex, -P/--pcre, and -F/--fixed-strings are mutually exclusive.");
        }

        var searchStdin = StdinSearch.ShouldSearch(bound);
        var patterns = LoadPatterns(bound);
        var casing = CaseFold.Decide(CaseFold.Resolve(result), patterns);
        var report = BindReport(result, filesWith, filesWithout, count, quiet);
        var beforeCount = ContextCount(result, before, context);
        var afterCount = ContextCount(result, after, context);
        if (beforeCount < 0 || afterCount < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        int? maxCountValue = null;
        if (IsSet(result, maxCount))
        {
            var parsedMax = result.GetValue(maxCount);
            if (parsedMax < 0)
            {
                throw new ArgumentException("-m/--max-count must be a non-negative integer.");
            }

            maxCountValue = parsedMax;
        }

        var parsedFormat = OutputFormats.Parse(result.GetValue(format));
        var colorOn = parsedFormat == OutputFormat.Text && TextColor.Enabled(TextColor.Parse(result.GetValue(color)));
        var wantStats = result.GetValue(stats);
        var forceHeading = result.GetValue(heading);
        var suppressHeading = result.GetValue(noHeading);
        var lineNumbers = ResolveSwitch(result, fallback: true, lineNumber, noLineNumber);
        var showColumn = ResolveSwitch(result, fallback: true, column, noColumn);
        if (result.GetValue(vimgrep))
        {
            lineNumbers = true;
            showColumn = true;
            forceHeading = false;
            suppressHeading = true;
        }

        // One file or stdin omits the path unless -H or --vimgrep. A directory or
        // several paths include it unless -I. The last of -H and -I wins, including
        // over the path --vimgrep would add. Headings need a path to print.
        var showFilename = ResolveSwitch(
            result,
            fallback: result.GetValue(vimgrep) || !IsOneFile(bound, searchStdin),
            withFilename,
            noFilename);
        if (!showFilename)
        {
            forceHeading = false;
            suppressHeading = true;
        }

        LineReplacer? replacer = null;
        if (parsedFormat == OutputFormat.Text && IsSet(result, replace) && !result.GetValue(invert))
        {
            replacer = LineReplacer.Create(
                result.GetValue(replace) ?? "",
                !result.GetValue(fixedStrings),
                patterns,
                casing.IgnoreCase,
                result.GetValue(pcre),
                result.GetValue(lineRegexp),
                result.GetValue(multilineDotAll),
                casing.PerPattern);
        }

        var oneRowPerLine = parsedFormat == OutputFormat.Text
            && report == ReportMode.Matches
            && !result.GetValue(onlyMatching)
            && !result.GetValue(vimgrep)
            && replacer == null;

        // Plain text with no color is printed straight from the file bytes: the line body is
        // already the UTF-8 the writer would encode. One pattern keeps the row order simple.
        var rawTextLines = oneRowPerLine
            && !colorOn
            && !result.GetValue(lineRegexp)
            && patterns.Count == 1
            && patterns[0].Length > 0
            && beforeCount == 0
            && afterCount == 0
            && !result.GetValue(invert)
            && !wantStats;

        SearchPresentation Present(bool heading) => new(
            report,
            result.GetValue(onlyMatching),
            colorOn,
            heading,
            lineNumbers,
            showColumn,
            result.GetValue(nullPaths),
            replacer,
            showFilename,
            oneRowPerLine);

        var autoHeading = parsedFormat == OutputFormat.Text
            && report == ReportMode.Matches
            && !suppressHeading
            && !forceHeading
            && TextColor.IsTerminal();
        var useHeading = parsedFormat == OutputFormat.Text
            && report == ReportMode.Matches
            && !suppressHeading
            && forceHeading;
        var noted = new HashSet<string>(StringComparer.Ordinal);
        void EmitNote(string? note)
        {
            if (string.IsNullOrEmpty(note) || !noted.Add(note))
            {
                return;
            }

            MachineOutput.Note(note);
        }

        MachineOutput.MatchStream? stream = null;
        SearchEvents? events = null;
        if (report != ReportMode.Quiet)
        {
            stream = new MachineOutput.MatchStream(
                parsedFormat,
                Present(useHeading),
                autoHeading);
            events = new SearchEvents
            {
                Note = EmitNote,
                Batch = stream.File,
            };
        }

        var roots = searchStdin ? [StandardInput.DisplayPath] : bound.Paths;
        var multiple = roots.Length > 1;
        var extra = patterns.Count > 1 ? patterns[1..] : [];

        // Only a matches report prints a line. Machine formats print text and lineText for
        // every report they support, and the matched slice is read only where it is shown.
        var needLineText = report == ReportMode.Matches || parsedFormat != OutputFormat.Text;
        var needMatchText = parsedFormat != OutputFormat.Text
            || (report == ReportMode.Matches
                && (colorOn || result.GetValue(onlyMatching) || replacer != null));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<SearchOutcome>(roots.Length);
        var pathFailed = false;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stdinRoot = searchStdin || (bound.PathExplicit && root == "-");
                var part = await Searcher.Search(new SearchQuery
                {
                    Pattern = patterns[0],
                    AdditionalPatterns = extra,
                    Path = stdinRoot ? StandardInput.DisplayPath : root,
                    DisplayRoot = stdinRoot ? null : Searcher.MultiRootDisplay(root, multiple),
                    SearchStdin = stdinRoot,
                    IndexDirectory = result.GetValue(index),
                    IgnoreCase = casing.IgnoreCase,
                    PatternIgnoreCase = casing.PerPattern,
                    // -E/--regex is an alias of this default. -F opts out. -P keeps regex and switches engines.
                    Regex = !result.GetValue(fixedStrings),
                    Backtracking = result.GetValue(pcre),
                    ForceScan = result.GetValue(scan),
                    WordRegexp = result.GetValue(word),
                    InvertMatch = result.GetValue(invert),
                    LineRegexp = result.GetValue(lineRegexp),
                    NoMessages = result.GetValue(noMessages),
                    NoIgnore = result.GetValue(noIgnore),
                    NoIgnoreVcs = result.GetValue(noIgnoreVcs),
                    IncludeHidden = result.GetValue(hidden),
                    FollowSymlinks = result.GetValue(follow),
                    SearchBinary = result.GetValue(binary),
                    SearchText = result.GetValue(text),
                    Multiline = result.GetValue(multiline),
                    MultilineDotAll = result.GetValue(multilineDotAll),
                    Stats = wantStats,
                    Report = report,
                    BeforeContext = beforeCount,
                    AfterContext = afterCount,
                    MaxCount = maxCountValue,
                    Globs = BindGlobs(result, glob, iglob),
                    IgnoreFiles = Values(result, ignoreFile),
                    Types = Values(result, type),
                    TypeNots = Values(result, typeNot),
                    TypeAdds = adds,
                    MaxDepth = MaxDepth(result, maxDepth),
                    MaxDegreeOfParallelism = ThreadCount(result, threads),
                    NeedLineText = needLineText,
                    NeedMatchText = needMatchText,
                    OneRowPerLine = oneRowPerLine,
                    RawTextLines = rawTextLines,
                    CancellationToken = cancellationToken,
                    Events = events,
                });
                parts.Add(part);
                EmitNote(part.Note);
                if (report == ReportMode.Quiet && !wantStats && part.MatchCount > 0)
                {
                    break;
                }
            }
            catch (SearchPathException ex)
            {
                pathFailed = true;
                if (!ex.Suppress)
                {
                    Console.Error.WriteLine(ex.Message);
                }
            }
        }

        if (parts.Count == 0)
        {
            return ExitCodes.Error;
        }

        var outcome = CombineOutcomes(parts, clock.ElapsedMilliseconds);

        if (stream != null)
        {
            stream.Complete(outcome);
        }
        else
        {
            var quietHeading = parsedFormat == OutputFormat.Text
                && report == ReportMode.Matches
                && !suppressHeading
                && (forceHeading || (TextColor.IsTerminal() && outcome.FilesSearched > 1));
            MachineOutput.Note(outcome.Note);
            MachineOutput.WriteMatches(
                outcome.Matches,
                parsedFormat,
                Present(quietHeading),
                outcome.FilesWithoutMatch,
                outcome.ElapsedMilliseconds);
        }

        if (wantStats)
        {
            MachineOutput.WriteStats(outcome);
        }

        var produced = report == ReportMode.FilesWithoutMatch
            ? outcome.WithoutCount > 0
            : outcome.MatchCount > 0;
        if (pathFailed)
        {
            return ExitCodes.Error;
        }

        return produced ? ExitCodes.Success : ExitCodes.NoMatches;
    }

    private static SearchOutcome CombineOutcomes(List<SearchOutcome> parts, long elapsedMilliseconds)
    {
        var searched = 0;
        var matched = 0;
        var lines = 0;
        var matches = 0;
        var without = 0;
        var workers = 1;
        var usedIndex = parts.Count > 0;
        var prefilter = false;
        foreach (var part in parts)
        {
            searched += part.FilesSearched;
            matched += part.FilesMatched;
            lines += part.LinesMatched;
            matches += part.MatchCount;
            without += part.WithoutCount;
            if (part.Workers > workers)
            {
                workers = part.Workers;
            }

            if (!part.UsedIndex)
            {
                usedIndex = false;
            }

            if (part.UsedTrigramPrefilter)
            {
                prefilter = true;
            }
        }

        return new SearchOutcome
        {
            Matches = [],
            UsedIndex = usedIndex,
            UsedTrigramPrefilter = prefilter,
            Confirmation = workers > 1 ? "parallel" : "sequential",
            Workers = workers,
            FilesSearched = searched,
            FilesMatched = matched,
            LinesMatched = lines,
            MatchCount = matches,
            WithoutCount = without,
            Streamed = true,
            ElapsedMilliseconds = elapsedMilliseconds,
        };
    }

    private static int ListFiles(
        System.CommandLine.ParseResult result,
        Option<string[]> glob,
        Option<string[]> iglob,
        Option<string[]> ignoreFile,
        Option<string[]> type,
        Option<string[]> typeNot,
        string[] typeAdds,
        Option<bool> noIgnore,
        Option<bool> noIgnoreVcs,
        Option<bool> hidden,
        Option<bool> quiet,
        Option<bool> noMessages,
        Option<bool> filesWith,
        Option<bool> filesWithout,
        Option<bool> count,
        Option<bool> nullPaths,
        CancellationToken cancellationToken)
    {
        if (result.GetValue(filesWith) || result.GetValue(filesWithout) || result.GetValue(count))
        {
            throw new ArgumentException("--files cannot be combined with -l/--files-with-matches, --files-without-match, or -c/--count.");
        }

        var bound = SearchSyntax.Bind(result, typeList: false, listFiles: true);
        var filter = PathFilter.Create(BindGlobs(result, glob, iglob), Values(result, type), Values(result, typeNot), typeAdds);
        List<string> paths;
        try
        {
            var policy = (result.GetValue(noIgnore)
                ? IgnorePolicy.None
                : result.GetValue(noIgnoreVcs) ? IgnorePolicy.CustomOnly : IgnorePolicy.All)
                .WithExtra(Values(result, ignoreFile));
            paths = FileList.Collect(
                bound.Paths[0],
                policy,
                includeHidden: result.GetValue(hidden),
                filter,
                result.GetValue(noMessages),
                cancellationToken);
        }
        catch (SearchPathException ex)
        {
            if (!ex.Suppress)
            {
                Console.Error.WriteLine(ex.Message);
            }

            return ExitCodes.Error;
        }

        if (!result.GetValue(quiet))
        {
            var presentation = new SearchPresentation(
                ReportMode.FilesWithMatches,
                OnlyMatching: false,
                Color: false,
                NullPaths: result.GetValue(nullPaths));
            foreach (var path in paths)
            {
                TextLayout.WriteFilePath(path, presentation);
            }
        }

        return paths.Count > 0 ? ExitCodes.Success : ExitCodes.NoMatches;
    }

    private static List<string> LoadPatterns(BoundSearch bound)
    {
        var patterns = new List<string>(bound.Patterns);
        foreach (var file in bound.PatternFiles)
        {
            patterns.AddRange(PatternFiles.Read(file));
        }

        if (patterns.Count == 0)
        {
            throw new ArgumentException("Pattern file contains no patterns.");
        }

        return patterns;
    }

    private static ReportMode BindReport(
        System.CommandLine.ParseResult result,
        Option<bool> filesWith,
        Option<bool> filesWithout,
        Option<bool> count,
        Option<bool> quiet)
    {
        var with = result.GetValue(filesWith);
        var without = result.GetValue(filesWithout);
        var counts = result.GetValue(count);
        var silent = result.GetValue(quiet);
        var selected = (with ? 1 : 0) + (without ? 1 : 0) + (counts ? 1 : 0) + (silent ? 1 : 0);
        if (selected > 1)
        {
            throw new ArgumentException("-l/--files-with-matches, --files-without-match, -c/--count, and -q/--quiet/--silent are mutually exclusive.");
        }

        if (with)
        {
            return ReportMode.FilesWithMatches;
        }

        if (without)
        {
            return ReportMode.FilesWithoutMatch;
        }

        if (counts)
        {
            return ReportMode.Count;
        }

        if (silent)
        {
            return ReportMode.Quiet;
        }

        return ReportMode.Matches;
    }

    /// <summary>
    /// <c>-g</c> and <c>--iglob</c> in command-line order. Option results alone would sort one flag before the other.
    /// </summary>
    private static GlobSpec[] BindGlobs(
        System.CommandLine.ParseResult result,
        Option<string[]> glob,
        Option<string[]> iglob)
    {
        var specs = new List<GlobSpec>();
        bool? pendingIgnoreCase = null;
        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.Option)
            {
                var raw = token.Value;
                var eq = raw.IndexOf('=');
                var name = eq >= 0 ? raw[..eq] : raw;
                if (name.Length > 2 && name[0] == '-' && name[1] != '-' && Names(glob, name[..2]))
                {
                    name = name[..2];
                }

                bool? ignoreCase = Names(iglob, name) ? true : Names(glob, name) ? false : null;
                if (ignoreCase == null)
                {
                    pendingIgnoreCase = null;
                    continue;
                }

                if (eq >= 0)
                {
                    specs.Add(new GlobSpec(raw[(eq + 1)..], ignoreCase.Value));
                    pendingIgnoreCase = null;
                    continue;
                }

                if (name == "-g" && raw.Length > 2)
                {
                    specs.Add(new GlobSpec(raw[2..], false));
                    pendingIgnoreCase = null;
                    continue;
                }

                pendingIgnoreCase = ignoreCase;
                continue;
            }

            if (token.Type == TokenType.Argument && pendingIgnoreCase != null)
            {
                specs.Add(new GlobSpec(token.Value, pendingIgnoreCase.Value));
                pendingIgnoreCase = null;
            }
        }

        return specs.Count == 0 ? [] : specs.ToArray();
    }

    private static string[] Values(System.CommandLine.ParseResult result, Option<string[]> option) =>
        result.GetValue(option) ?? [];

    private static int ContextCount(System.CommandLine.ParseResult result, Option<int> side, Option<int> both)
    {
        if (IsSet(result, side))
        {
            return result.GetValue(side);
        }

        if (IsSet(result, both))
        {
            return result.GetValue(both);
        }

        return 0;
    }

    private static int? MaxDepth(System.CommandLine.ParseResult result, Option<int> maxDepth)
    {
        if (!IsSet(result, maxDepth))
        {
            return null;
        }

        return result.GetValue(maxDepth);
    }

    private static int ThreadCount(System.CommandLine.ParseResult result, Option<int> threads)
    {
        if (!IsSet(result, threads))
        {
            return 0;
        }

        var value = result.GetValue(threads);
        if (value < 1)
        {
            throw new ArgumentException("--threads must be a positive integer.");
        }

        return value;
    }

    /// <summary>
    /// Last explicit flag wins. <paramref name="enable"/> and <paramref name="disable"/> are a pair
    /// such as <c>-n</c> / <c>-N</c>. When neither appears, <paramref name="fallback"/> is used.
    /// </summary>
    /// <summary>
    /// True when text output should omit the path by default: stdin, or exactly one
    /// path that is not a directory. A directory is not one file, even when it holds
    /// a single file. A missing path is not a directory.
    /// </summary>
    private static bool IsOneFile(BoundSearch bound, bool searchStdin)
    {
        if (searchStdin)
        {
            return true;
        }

        if (!bound.PathExplicit || bound.Paths.Length != 1)
        {
            return false;
        }

        var path = bound.Paths[0];
        if (path == "-")
        {
            return true;
        }

        return !Directory.Exists(path);
    }

    private static bool ResolveSwitch(
        System.CommandLine.ParseResult result,
        bool fallback,
        Option<bool> enable,
        Option<bool> disable)
    {
        bool? value = null;
        foreach (var token in result.Tokens)
        {
            if (token.Type != TokenType.Option)
            {
                continue;
            }

            // Aliases omits the option's Name, so --no-column (a name with no short alias)
            // is invisible if only Aliases is checked.
            if (Names(enable, token.Value))
            {
                value = true;
            }
            else if (Names(disable, token.Value))
            {
                value = false;
            }
        }

        return value ?? fallback;
    }

    private static bool Names(Option option, string token)
    {
        var name = token;
        var eq = name.IndexOf('=');
        if (eq >= 0)
        {
            name = name[..eq];
        }

        if (string.Equals(name, option.Name, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var alias in option.Aliases)
        {
            if (string.Equals(name, alias, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSet(System.CommandLine.ParseResult result, Option option)
    {
        var parsed = result.GetResult(option);
        return parsed != null && parsed.Tokens.Count > 0;
    }
}
