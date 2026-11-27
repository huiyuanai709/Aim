namespace Ngrep.Output;

internal enum OutputFormat
{
    Text,
    Json,
    Ndjson,
    RgJson,
}

internal static class OutputFormats
{
    public static OutputFormat Parse(string? value)
    {
        return (value ?? "text").ToLowerInvariant() switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            "ndjson" => OutputFormat.Ndjson,
            "rgjson" => OutputFormat.RgJson,
            _ => throw new ArgumentException($"Unknown format '{value}'. Expected text, json, ndjson, or rgjson."),
        };
    }
}
