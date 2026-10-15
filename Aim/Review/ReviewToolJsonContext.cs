using System.Text.Json.Serialization;

namespace Aim;

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
internal partial class ReviewToolJsonContext : JsonSerializerContext
{
}
