using System.Runtime.CompilerServices;

namespace Formidable;

/// <summary>
/// Opaque identity of one validation rule, produced by
/// <see cref="IRuleLevelValidator{TModel}.SelectRules"/> and consumed by
/// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/>. Suitable as a dictionary key:
/// equal identities hash equally, and identity is stable for the lifetime of the validator
/// that produced it.
/// </summary>
/// <remarks>
/// Identities are validator-instance-scoped: equality and hashing follow the wrapped key by
/// reference, so two validator instances built from the same class yield identities that never
/// match, and an identity is only meaningful passed back to the validator that produced it.
/// </remarks>
public readonly struct RuleIdentity : IEquatable<RuleIdentity>
{
    private readonly object? _key;

    /// <summary>Wraps the given key object; the key's reference is the identity.</summary>
    public RuleIdentity(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    /// <summary>
    /// The wrapped key, or <see langword="null"/> for a <see langword="default"/> identity.
    /// Only meaningful to the validator that produced it: an
    /// <see cref="IRuleLevelValidator{TModel}"/> implementation resolves an identity handed to
    /// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/> by reading back here the rule
    /// object it wrapped in <see cref="IRuleLevelValidator{TModel}.SelectRules"/>, with no side
    /// lookup. Any other reader holds an object whose type and content promise nothing.
    /// </summary>
    public object? Key => _key;

    /// <inheritdoc />
    public bool Equals(RuleIdentity other) => ReferenceEquals(_key, other._key);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RuleIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _key is null ? 0 : RuntimeHelpers.GetHashCode(_key);

    /// <summary>Reference equality on the underlying keys.</summary>
    public static bool operator ==(RuleIdentity left, RuleIdentity right) => left.Equals(right);

    /// <summary>Reference inequality on the underlying keys.</summary>
    public static bool operator !=(RuleIdentity left, RuleIdentity right) => !left.Equals(right);
}
