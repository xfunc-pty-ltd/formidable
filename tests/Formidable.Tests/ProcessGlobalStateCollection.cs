namespace Formidable.Tests;

/// <summary>
/// Serializes the test classes that write process-global state — FluentValidation's global
/// validator-selector factory and the default thread cultures — against one another. Each such
/// class already restores what it wrote, which settles the value but not the window: xunit runs
/// one class's <c>[Fact]</c>s one at a time on its own, and runs different classes in parallel,
/// so a restore says nothing about what another class read while the write stood. This collection
/// is the coordination for that half, and its reach is exactly its membership — a class that
/// writes process-global state, or reads state a member of this collection writes, belongs in it,
/// and a class outside it is not protected by it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessGlobalStateCollection
{
    /// <summary>The collection name, so a member cannot join by mistyping it.</summary>
    public const string Name = "Process-global state";
}
