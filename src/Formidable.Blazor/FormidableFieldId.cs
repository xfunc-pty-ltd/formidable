using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Deterministic element ids for fields, keyed by the owning object instance and the field name, shared by the kit's inputs, the message lists and the focus service.</summary>
public static class FormidableFieldId
{
    private const string MessagesSuffix = "-messages";

    /// <summary>The field's element id, <c>formidable-{owner-hash}-{name-hash}-{sanitized-name}</c>, with <c>form</c> as the model-level field's name segment.</summary>
    /// <param name="field">The field whose element id is being computed.</param>
    /// <returns>The id, the same for one instance and name for as long as the instance lives; its two name segments are the same on every run of the app, its owner segment is not.</returns>
    /// <remarks>
    /// The sanitized name (letters and digits lowercased, everything else <c>-</c>) is the suffix
    /// a stylesheet or a locator selects on; the owner segment names the instance and is nothing
    /// a consumer can write.
    /// </remarks>
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{NameHash(field.FieldName):x8}-{name}";
    }

    /// <summary>FNV-1a over the name's UTF-16 code units, stable across processes.</summary>
    /// <param name="name">The field name, case and punctuation intact.</param>
    /// <returns>The 32-bit hash.</returns>
    // Spelled out rather than taken from string.GetHashCode(), which is randomized per process: a
    // stylesheet, a locator or a test computing the expected id on its own would otherwise get a
    // different answer on every run of the app. Thirty-two bits over the names one object owns
    // makes two ids overwhelmingly likely to differ, where the sanitizer collision it separates
    // (Url and URL; Address.City and Address_City) is structural and happens every time.
    private static uint NameHash(string name)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in name)
        {
            unchecked
            {
                hash = (hash ^ (byte)c) * prime;
                hash = (hash ^ (byte)(c >> 8)) * prime;
            }
        }

        return hash;
    }

    /// <summary>The element id for the field a member-access expression names, such as <c>o =&gt; o.Description</c>, with the object part evaluated against <paramref name="model"/> as <see cref="FieldIdentifier.Create{TField}"/> evaluates its own.</summary>
    /// <typeparam name="TModel">The type the accessor starts from: the field's owner, or the root of the path to it.</typeparam>
    /// <typeparam name="TValue">The accessor's type: the field's own value type, or <c>object</c> where a caller forwards an <c>Expression&lt;Func&lt;TModel, object&gt;&gt;</c>, whose boxing convert is read past.</typeparam>
    /// <param name="model">The instance the accessor is evaluated against.</param>
    /// <param name="accessor">A member-access expression naming the field.</param>
    /// <returns>The id <see cref="For(FieldIdentifier)"/> gives for that field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="accessor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The body is not a member access (<c>o =&gt; !o.Flag</c>), names a static member, or the field's owner reads as <see langword="null"/>; a plain property-or-field chain reports a <see langword="null"/> met on the way the same way, where an object part holding an indexer or a method call throws <see cref="NullReferenceException"/> for one.</exception>
    /// <remarks>
    /// There is no expression shape for the model-level field: construct
    /// <c>new FieldIdentifier(model, string.Empty)</c> and pass it to
    /// <see cref="For(FieldIdentifier)"/>. A member chain rooted at the lambda's parameter is read
    /// by reflection; an object part holding an indexer or a method call compiles the accessor,
    /// which costs orders of magnitude more, so hold that id in a field rather than computing it
    /// per render.
    /// </remarks>
    public static string For<TModel, TValue>(TModel model, Expression<Func<TModel, TValue>> accessor)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(accessor);

        // A value-typed member forwarded as Expression<Func<TModel, object>> arrives behind a
        // boxing convert; a reference-typed one carries no convert node at all. Only that node is
        // read past: `!o.Flag` and `-o.Count` are UnaryExpressions too, and neither names a field.
        var body = accessor.Body is UnaryExpression { NodeType: ExpressionType.Convert } boxed
            ? boxed.Operand
            : accessor.Body;

        if (body is not MemberExpression member)
        {
            throw new ArgumentException(
                $"'{accessor}' is not a member access expression, e.g. 'o => o.Description'.",
                nameof(accessor));
        }

        return For(new FieldIdentifier(OwnerOf(member, model, accessor), member.Member.Name));
    }

    /// <summary>The instance the named member is read from: the field's owner, and half of its id.</summary>
    /// <typeparam name="TModel">The type the accessor starts from.</typeparam>
    /// <typeparam name="TValue">The accessor's type.</typeparam>
    /// <param name="member">The member access the accessor's body is.</param>
    /// <param name="model">The instance the accessor is evaluated against.</param>
    /// <param name="accessor">The whole accessor, for the message and the parameter of a compiled read.</param>
    /// <returns>The owner, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The member is static, or the object part reads through a <see langword="null"/>.</exception>
    private static object OwnerOf<TModel, TValue>(
        MemberExpression member,
        TModel model,
        Expression<Func<TModel, TValue>> accessor)
        where TModel : class
    {
        var objectPart = member.Expression ?? throw new ArgumentException(
            $"'{accessor}' names a static member, which has no owning instance to key an id by.",
            nameof(accessor));

        // o => o.Member: the lambda's own parameter is the owner, so nothing is evaluated at all.
        if (objectPart is ParameterExpression)
        {
            return model;
        }

        var owner = TryWalkMemberChain(objectPart, model, out var walked)
            ? walked
            : Expression.Lambda<Func<TModel, object?>>(
                Expression.Convert(objectPart, typeof(object)),
                accessor.Parameters).Compile()(model);

        return owner ?? throw new ArgumentException(
            $"'{accessor}' reads through a null, so the field has no owner yet to key an id by.",
            nameof(accessor));
    }

    /// <summary>Reads a property-or-field chain such as <c>o.A.B</c> off <paramref name="model"/> by reflection, or answers <see langword="false"/> for any other shape, leaving the caller to compile.</summary>
    /// <typeparam name="TModel">The type the chain starts from.</typeparam>
    /// <param name="objectPart">The accessor body's object part.</param>
    /// <param name="model">The instance the chain is read off.</param>
    /// <param name="owner">The object the chain reaches, or <see langword="null"/> where it reads through one.</param>
    /// <returns><see langword="false"/> when the chain is rooted anywhere but the lambda's parameter or passes through anything but a property or a field.</returns>
    private static bool TryWalkMemberChain<TModel>(Expression objectPart, TModel model, out object? owner)
        where TModel : class
    {
        owner = null;

        var chain = new Stack<MemberExpression>();
        var node = objectPart;
        while (node is MemberExpression link)
        {
            if (link.Member is not (PropertyInfo or FieldInfo))
            {
                return false;
            }

            chain.Push(link);
            node = link.Expression!;
        }

        if (node is not ParameterExpression)
        {
            return false;
        }

        object? current = model;
        foreach (var link in chain)
        {
            if (current is null)
            {
                break;
            }

            current = link.Member is PropertyInfo property
                ? property.GetValue(current)
                : ((FieldInfo)link.Member).GetValue(current);
        }

        owner = current;
        return true;
    }

    /// <summary>The id of the element listing a field's messages, which the message components render and a kit input's <c>aria-describedby</c> targets: <see cref="For(FieldIdentifier)"/> plus <c>-messages</c>.</summary>
    /// <param name="field">The field whose message list is being addressed.</param>
    /// <returns>The message-list id.</returns>
    /// <remarks>
    /// Call it rather than appending the suffix yourself, so a control wired by hand and the list
    /// it describes cannot drift apart; <see cref="FormidableFieldContext.AriaDescribedBy"/> hands
    /// the same string to a hand-rolled control.
    /// </remarks>
    public static string MessagesFor(FieldIdentifier field) => MessagesFor(For(field));

    /// <summary>The message-list id for a caller that already holds the field's element id.</summary>
    /// <param name="elementId">The element id <see cref="For(FieldIdentifier)"/> gave.</param>
    /// <returns>That id plus <c>-messages</c>.</returns>
    // The id and its message-list id are computed together at registration, so the suffix is
    // appended without deriving the id a second time.
    internal static string MessagesFor(string elementId) => elementId + MessagesSuffix;
}
