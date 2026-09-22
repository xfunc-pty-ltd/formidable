using System.Runtime.CompilerServices;

namespace Formidable;

/// <summary>The opaque identity of one rule, produced by <see cref="IRuleLevelValidator{TModel}.SelectRules"/> and handed back to <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/>; usable as a dictionary key.</summary>
/// <remarks>
/// Equality and hashing follow the wrapped key by reference: identities from two validators
/// match only when both wrap the same key instance, and an identity is stable for, and
/// meaningful only to, the validator that produced it.
/// </remarks>
public readonly struct RuleIdentity : IEquatable<RuleIdentity>
{
    private readonly object? _key;

    /// <summary>Wraps <paramref name="key"/>, whose reference is the identity.</summary>
    /// <param name="key">The object whose reference is the identity; the producing validator reads it back through <see cref="Key"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public RuleIdentity(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    /// <summary>The wrapped key, or <see langword="null"/> for a <see langword="default"/> identity; meaningful only to the validator that produced it.</summary>
    /// <remarks>
    /// An <see cref="IRuleLevelValidator{TModel}"/> implementation resolves an identity handed to
    /// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/> by reading back here the rule
    /// object it wrapped in <see cref="IRuleLevelValidator{TModel}.SelectRules"/>, with no side
    /// lookup. Any other reader holds an object whose type and content promise nothing.
    /// </remarks>
    public object? Key => _key;

    /// <summary>Whether both identities wrap the same key instance.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns><see langword="true"/> when the wrapped keys are the same reference.</returns>
    public bool Equals(RuleIdentity other) => ReferenceEquals(_key, other._key);

    /// <summary>Whether <paramref name="obj"/> is a <see cref="RuleIdentity"/> wrapping the same key instance.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an identity over the same reference.</returns>
    public override bool Equals(object? obj) => obj is RuleIdentity other && Equals(other);

    /// <summary>The wrapped key's reference hash, or 0 for a <see langword="default"/> identity.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => _key is null ? 0 : RuntimeHelpers.GetHashCode(_key);

    /// <summary>Reference equality on the wrapped keys.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when both wrap the same reference.</returns>
    public static bool operator ==(RuleIdentity left, RuleIdentity right) => left.Equals(right);

    /// <summary>Reference inequality on the wrapped keys.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when the wrapped references differ.</returns>
    public static bool operator !=(RuleIdentity left, RuleIdentity right) => !left.Equals(right);
}
