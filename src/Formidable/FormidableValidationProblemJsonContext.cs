using System.Text.Json;
using System.Text.Json.Serialization;

namespace Formidable;

/// <summary>The generated JSON metadata for <see cref="FormidableValidationProblem"/>: pass <c>Default.FormidableValidationProblem</c> to <c>ReadFromJsonAsync</c> and the read needs no reflection.</summary>
/// <remarks>
/// A Blazor WebAssembly Release publish trims, and trimming removes the constructor that
/// reflection-based deserialization calls, so the plain generic read throws
/// <see cref="System.NotSupportedException"/> there while working everywhere else. This metadata
/// carries the constructor and the property names, and it takes its options from
/// <see cref="JsonSerializerDefaults.Web"/>, which is what the plain generic read applies, so the
/// body reads identically either side of a trim.
/// </remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FormidableValidationProblem))]
public sealed partial class FormidableValidationProblemJsonContext : JsonSerializerContext
{
}
