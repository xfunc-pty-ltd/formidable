using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Deterministic DOM element ids for fields, shared by inputs (element id), messages
/// (aria-describedby target), and the focus service. An id is keyed by the owning object
/// instance and the field name together, and the name reaches it twice: once as a hash, which
/// is what makes two names differ, and once sanitized, which is what makes the id legible.
/// </summary>
public static class FormidableFieldId
{
    private const string MessagesSuffix = "-messages";

    /// <summary>
    /// The id for a field: <c>formidable-{owner-hash}-{name-hash}-{sanitized-name}</c>; the
    /// model-level field uses <c>form</c> as its name segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name hash sits before the sanitized name so that the name stays the id's suffix. The
    /// owner segment is an object-identity hash, which is opaque rather than addressable: it
    /// names the instance, so a different model or a different collection row gives a different
    /// one (all but certainly — the last paragraph is the exception), and its value is drawn
    /// from a sequence the runtime advances on each first identity-hash request, so anything
    /// the app asks about earlier moves it along. Nothing a consumer writes can name it. The
    /// suffix is the part that stays put, and it is what a consumer's CSS and a hand-written
    /// browser locator select on (<c>[id$='-description']</c>).
    /// </para>
    /// <para>
    /// Sanitization lowercases letters and digits and replaces every other character with
    /// <c>-</c>, so names that differ only in case or in punctuation — <c>Url</c> and <c>URL</c>,
    /// <c>Address.City</c> and <c>Address_City</c> — sanitize alike. The hash is taken from the
    /// original name, case and all, which is what separates them again. It is spelled out here
    /// rather than taken from <see cref="string.GetHashCode()"/>, which — unlike the identity
    /// hash beside it — really is randomized per process: a stylesheet, a locator, or a test
    /// computing the expected id independently would otherwise get a different answer on every
    /// run of the app. Thirty-two bits over the field names one object owns makes two ids
    /// overwhelmingly likely to differ rather than certain to — where the sanitizer collision it
    /// replaces is structural, and happens every time.
    /// </para>
    /// <para>
    /// The owner segment prints in the same eight hex digits and is narrower than it looks. The
    /// runtime keeps an object's identity hash in part of the object's header rather than in a
    /// full <see cref="int"/> — twenty-six bits of it on CoreCLR, the runtime under Blazor
    /// Server and every server-side render — so among enough owner objects rendered at once two
    /// can draw the same value, and their same-named fields then render the same id. The odds
    /// climb with the square of the count: negligible for the hundreds of rows a form usually
    /// shows, under one percent at a thousand rendered at once, about one in six at five
    /// thousand. Validation is untouched, since the engine tells fields apart by the owner
    /// reference and never by this hash; what a duplicate id disturbs is whatever is keyed by
    /// the id. A site that reaches an element through it reaches the first of the two in the
    /// document: a summary click or a blocked submit's focus lands on the first row, the second
    /// row's <c>aria-describedby</c> names the first row's message list, and the value sync on
    /// blur writes into the first row's box. The field-order service keys its answer by id as
    /// well, and there a shared id can name only one field: the one the registry lists later
    /// keeps it and takes the first row's place in the resolved order, and the other never
    /// enters that order, so its issues sort after every placed field's. Virtualizing a form
    /// that large keeps the rendered set to the rows in view, which is the count that matters.
    /// </para>
    /// </remarks>
    /// <param name="field">The field whose element id is being computed.</param>
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{NameHash(field.FieldName):x8}-{name}";
    }

    /// <summary>
    /// FNV-1a over the name's UTF-16 code units. Spelled out rather than delegated: see the
    /// remarks on <see cref="For(FieldIdentifier)"/> for why a per-process hash cannot serve here.
    /// </summary>
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

    /// <summary>
    /// The id for a field named by a member-access expression, e.g. <c>o =&gt; o.Description</c> —
    /// the same id <see cref="For(FieldIdentifier)"/> gives for the field that expression names,
    /// without the stringly-typed <c>nameof</c> step. There is no expression shape for the
    /// model-level field (an empty field name): construct that <see cref="FieldIdentifier"/>
    /// directly and pass it to <see cref="For(FieldIdentifier)"/> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object part is evaluated, the way <see cref="FieldIdentifier.Create{TField}"/>
    /// evaluates its own: <c>o =&gt; o.Address.City</c> names the field the component rendering
    /// <c>Address.City</c> owns — <c>(model.Address, "City")</c> — rather than
    /// <c>(model, "City")</c>, which nothing renders. Reading through a null on the way there
    /// throws, because the field has no owner yet; so does a body that is not a member access,
    /// which is why <c>o =&gt; !o.Flag</c> is rejected rather than read as <c>Flag</c>.
    /// </para>
    /// <para>
    /// A member chain rooted at the lambda's parameter is walked by reflection and compiles
    /// nothing, so the shape every caller writes today — an id computed in a property getter,
    /// once per render — stays cheap. An object part that is not such a chain (an indexer, a
    /// method call) is compiled instead, which costs orders of magnitude more; hold that id in a
    /// field rather than recomputing it per render.
    /// </para>
    /// </remarks>
    /// <typeparam name="TModel">The type the accessor starts from: the field's owner, or the root of the path to it.</typeparam>
    /// <typeparam name="TValue">
    /// The accessor's type: the field's own value type, or <c>object</c> where a caller forwards
    /// an <c>Expression&lt;Func&lt;TModel, object&gt;&gt;</c>, whose boxing convert this method
    /// reads past.
    /// </typeparam>
    /// <param name="model">The instance the accessor is evaluated against.</param>
    /// <param name="accessor">A member-access expression naming the field, e.g. <c>o =&gt; o.Description</c>.</param>
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

    /// <summary>The instance the named member is read from — the field's owner, and half of its id.</summary>
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

    /// <summary>
    /// Reads <c>o.A.B</c> off <paramref name="model"/> by reflection. Answers
    /// <see langword="false"/> — leaving the caller to compile — when the chain is rooted anywhere
    /// but the lambda's parameter, or passes through anything but a property or a field.
    /// </summary>
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

    /// <summary>
    /// The id of the element listing a field's messages: <see cref="For(FieldIdentifier)"/> with a
    /// <c>-messages</c> suffix. This is the <c>aria-describedby</c> contract, and this method owns
    /// it: <see cref="FormidableFieldMessage{TValue}"/>,
    /// <see cref="FormidableCollectionMessage{TValue}"/> and <see cref="FormidableModelMessage"/>
    /// render this id on their lists, every kit
    /// input points <c>aria-describedby</c> at it while the field has issues, and
    /// <see cref="FormidableFieldContext.AriaDescribedBy"/> hands it to a hand-rolled control.
    /// Call this rather than concatenating the suffix, so a control wired by hand and the message
    /// list it describes cannot drift apart.
    /// </summary>
    /// <param name="field">The field whose message list is being addressed.</param>
    public static string MessagesFor(FieldIdentifier field) => MessagesFor(For(field));

    /// <summary>
    /// The same contract, for a caller that already holds the field's element id — the id and its
    /// message-list id are computed together at registration, so the suffix is appended without
    /// re-deriving the id.
    /// </summary>
    internal static string MessagesFor(string elementId) => elementId + MessagesSuffix;
}
