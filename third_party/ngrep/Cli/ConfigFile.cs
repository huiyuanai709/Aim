using System.Text;

namespace Ngrep.Cli;

internal readonly record struct LoadedConfig(string Path, string[] Arguments);

/// <summary>
/// <c>NGREP_CONFIG_PATH</c> is one argument per line, matching ripgrep's config file.
/// Blank lines and lines whose first non-whitespace character is <c>#</c> are ignored.
/// <c>RIPGREP_CONFIG_PATH</c> is not read.
/// <c>--no-config</c> on the command line skips this file entirely; the flag is
/// recognized before the file is opened.
/// </summary>
internal static class ConfigFile
{
    internal const string Variable = "NGREP_CONFIG_PATH";

    internal static string[] Apply(string[] args, out LoadedConfig? loaded)
    {
        loaded = null;
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(path) || SearchSyntax.SkipsConfig(args))
        {
            return args;
        }

        var configArgs = Read(path);
        loaded = new LoadedConfig(path, configArgs);
        return configArgs.Length == 0 ? args : SearchSyntax.PrependConfig(args, configArgs);
    }

    internal static bool Blames(string message, string[] configArgs)
    {
        foreach (var arg in configArgs)
        {
            if (arg.Length < 2 || arg[0] != '-')
            {
                continue;
            }

            var name = arg;
            var eq = arg.IndexOf('=');
            if (eq > 1)
            {
                name = arg[..eq];
            }

            if (message.Contains(arg, StringComparison.Ordinal) || message.Contains(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string[] Parse(string text) => Parse(text, null);

    internal static string[] Parse(string text, string? path)
    {
        var args = new List<string>();
        var lineNumber = 0;
        var start = 0;
        while (start <= text.Length)
        {
            lineNumber++;
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var line = text.AsSpan(start, end - start).Trim();
            if (line.Length > 0 && line[0] != '#')
            {
                if (path != null && line[0] == '-')
                {
                    if (line.Contains(' '))
                    {
                        throw new InvalidOperationException(
                            $"NGREP_CONFIG_PATH: {path}:{lineNumber}: '{line.ToString()}' contains whitespace. Each line is one argument; put the value on the next line.");
                    }

                    if (!SearchSyntax.IsKnownFlag(line.ToString()))
                    {
                        throw new InvalidOperationException(
                            $"NGREP_CONFIG_PATH: {path}:{lineNumber}: unknown flag '{line.ToString()}'.");
                    }
                }

                args.Add(line.ToString());
            }

            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
        }

        return args.ToArray();
    }

    private static string[] Read(string path)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return Parse(File.ReadAllText(path, utf8), path);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"NGREP_CONFIG_PATH: '{path}' is not valid UTF-8.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"NGREP_CONFIG_PATH: cannot read '{path}': {ex.Message}");
        }
    }
}
