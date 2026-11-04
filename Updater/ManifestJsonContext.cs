using System.Collections.Generic;
using System.Text.Json.Serialization;

// NativeAOT cannot use reflection-based JSON metadata for the update manifest.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ManifestJsonContext : JsonSerializerContext
{
}
