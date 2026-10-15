using System.Text.Json.Serialization;

namespace Aim;

internal sealed class ReviewComment
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("start_line")]
    public int StartLine { get; set; }

    [JsonPropertyName("end_line")]
    public int EndLine { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("suggestion_code")]
    public string? SuggestionCode { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ReviewComment>))]
[JsonSerializable(typeof(ReviewComment))]
internal partial class ReviewJsonContext : JsonSerializerContext
{
}
