namespace Ngrep.Search;

internal sealed class PathFilter
{
    public static PathFilter AcceptAll { get; } = new(GlobFilter.Empty, types: null, [], []);

    private readonly GlobFilter _globs;
    private readonly FileTypes? _types;
    private readonly string[] _includeTypes;
    private readonly string[] _excludeTypes;

    private PathFilter(GlobFilter globs, FileTypes? types, string[] includeTypes, string[] excludeTypes)
    {
        _globs = globs;
        _types = types;
        _includeTypes = includeTypes;
        _excludeTypes = excludeTypes;
    }

    public static PathFilter Create(IReadOnlyList<GlobSpec> globs, IReadOnlyList<string> types, IReadOnlyList<string> typeNots, IReadOnlyList<string> typeAdds)
    {
        if (globs.Count == 0 && types.Count == 0 && typeNots.Count == 0 && typeAdds.Count == 0)
        {
            return AcceptAll;
        }

        var fileTypes = FileTypes.Create(typeAdds);
        fileTypes.EnsureKnown(types);
        fileTypes.EnsureKnown(typeNots);
        return new PathFilter(
            GlobFilter.Parse(globs),
            fileTypes,
            types as string[] ?? types.ToArray(),
            typeNots as string[] ?? typeNots.ToArray());
    }

    public bool Accepts(string relativePath)
    {
        if (!_globs.Accepts(relativePath))
        {
            return false;
        }

        if (_types == null)
        {
            return true;
        }

        if (_includeTypes.Length > 0 && !_types.MatchesAny(_includeTypes, relativePath))
        {
            return false;
        }

        if (_excludeTypes.Length > 0 && _types.MatchesAny(_excludeTypes, relativePath))
        {
            return false;
        }

        return true;
    }
}
