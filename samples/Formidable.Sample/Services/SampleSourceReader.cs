using System.Collections.Concurrent;
using System.Reflection;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Services;

// Serves the ACTUAL source of pages and validators, embedded at build time, so the
// "Show the code" panels can never drift from what runs. Lookup is by file name
// ("DraftedBrief.cs", "Profiles.razor") against the manifest resource names of the
// sample and shared assemblies.
public sealed class SampleSourceReader
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(SampleSourceReader).Assembly,
        typeof(QuickContact).Assembly
    ];

    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string Read(string fileName) => _cache.GetOrAdd(fileName, static name =>
    {
        var suffix = "." + name;
        foreach (var assembly in Assemblies)
        {
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith(suffix, StringComparison.Ordinal));
            if (resource is null)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        return $"// Source for '{name}' was not embedded — check the csproj EmbeddedResource globs.";
    });
}
