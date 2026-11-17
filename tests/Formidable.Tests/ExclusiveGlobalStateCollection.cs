namespace Formidable.Tests;

/// <summary>
/// Runs its members alone, after every parallel collection has finished, for a test that writes
/// process-global state every validation reads. FluentValidation's global default severity is one:
/// it decides the severity of every failure from a rule with no severity of its own, so while a
/// test holds it at a warning, any class validating in parallel sees its errors turn into warnings.
/// FluentValidation's global ruleset-selector factory is another: a set of rules run apart asks
/// the factory's selector about its child and dependent rules, and a selector of any type but
/// FluentValidation's own marks each such answer as holding for its profile alone, so a class
/// reading that mark in parallel would see it flip. Membership in
/// <see cref="ProcessGlobalStateCollection"/> cannot coordinate either, because its reach is its
/// members and every class that validates would have to join it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveGlobalStateCollection
{
    /// <summary>The collection name, so a member cannot join by mistyping it.</summary>
    public const string Name = "Process-global state, run alone";
}
