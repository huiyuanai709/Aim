using Ngrep.Ignore;

namespace Ngrep.Search;

/// <summary>
/// Built-in file types plus <c>--type-add name:*.ext[,*.ext]</c> (optional <c>include:</c> prefix).
/// Type names are matched case-insensitively. Globs are case-sensitive, like gitignore.
/// </summary>
internal sealed class FileTypes
{
    private static readonly (string Name, string[] Globs)[] Builtin =
    [
        ("c", ["*.c", "*.h"]),
        ("cpp", ["*.cpp", "*.cc", "*.cxx", "*.hpp", "*.hh", "*.hxx"]),
        ("cs", ["*.cs", "*.csx"]),
        ("css", ["*.css", "*.scss"]),
        ("go", ["*.go"]),
        ("html", ["*.html", "*.htm"]),
        ("java", ["*.java"]),
        ("js", ["*.js", "*.mjs", "*.cjs", "*.jsx"]),
        ("json", ["*.json"]),
        ("kt", ["*.kt", "*.kts"]),
        ("md", ["*.md", "*.markdown"]),
        ("php", ["*.php"]),
        ("py", ["*.py", "*.pyi"]),
        ("rb", ["*.rb"]),
        ("rs", ["*.rs"]),
        ("sh", ["*.sh", "*.bash"]),
        ("sql", ["*.sql"]),
        ("swift", ["*.swift"]),
        ("toml", ["*.toml"]),
        ("ts", ["*.ts", "*.tsx", "*.mts", "*.cts"]),
        ("txt", ["*.txt"]),
        ("vue", ["*.vue"]),
        ("xml", ["*.xml"]),
        ("yml", ["*.yml", "*.yaml"]),
    ];

    private readonly Dictionary<string, List<IgnoreRule>> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _display = new(StringComparer.Ordinal);

    public static FileTypes Create(IReadOnlyList<string> typeAdds)
    {
        var types = new FileTypes();
        foreach (var (name, globs) in Builtin)
        {
            types.AddGlobs(name, globs);
        }

        foreach (var spec in typeAdds)
        {
            types.AddSpec(spec);
        }

        return types;
    }

    public void EnsureKnown(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!_rules.ContainsKey(Normalize(name)))
            {
                throw new ArgumentException($"Unknown file type '{name}'. Run ngrep --type-list.");
            }
        }
    }

    public bool Matches(string typeName, string relativePath)
    {
        if (!_rules.TryGetValue(Normalize(typeName), out var rules))
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (rule.Matches(relativePath, false))
            {
                return true;
            }
        }

        return false;
    }

    public bool MatchesAny(IReadOnlyList<string> typeNames, string relativePath)
    {
        foreach (var name in typeNames)
        {
            if (Matches(name, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<string> ListLines()
    {
        foreach (var name in _display.Keys.OrderBy(static n => n, StringComparer.Ordinal))
        {
            yield return name + ": " + string.Join(", ", _display[name]);
        }
    }

    private void AddSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException("Invalid --type-add. Expected name:*.ext[,*.ext].");
        }

        var colon = spec.IndexOf(':');
        if (colon <= 0 || colon == spec.Length - 1)
        {
            throw new ArgumentException($"Invalid --type-add '{spec}'. Expected name:*.ext[,*.ext].");
        }

        var name = spec[..colon];
        var rest = spec[(colon + 1)..];
        if (rest.StartsWith("include:", StringComparison.Ordinal))
        {
            rest = rest["include:".Length..];
        }

        if (rest.Length == 0)
        {
            throw new ArgumentException($"Invalid --type-add '{spec}'. Expected name:*.ext[,*.ext].");
        }

        var parts = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Invalid --type-add '{spec}'. Expected name:*.ext[,*.ext].");
        }

        var globs = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            globs[i] = NormalizeGlob(parts[i]);
        }

        AddGlobs(name, globs);
    }

    private void AddGlobs(string name, IReadOnlyList<string> globs)
    {
        var key = Normalize(name);
        if (key.Length == 0 || key.Contains(':'))
        {
            throw new ArgumentException($"Invalid file type name '{name}'.");
        }

        if (!_rules.TryGetValue(key, out var rules))
        {
            rules = [];
            _rules[key] = rules;
            _display[key] = [];
        }

        foreach (var glob in globs)
        {
            var rule = IgnoreRule.TryParse(glob)
                ?? throw new ArgumentException($"Invalid type glob '{glob}'.");
            rules.Add(rule);
            _display[key].Add(glob);
        }
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    private static string NormalizeGlob(string part)
    {
        if (part.Contains('*') || part.Contains('?') || part.Contains('[') || part.Contains('/'))
        {
            return part;
        }

        return "*." + part.TrimStart('.');
    }
}
