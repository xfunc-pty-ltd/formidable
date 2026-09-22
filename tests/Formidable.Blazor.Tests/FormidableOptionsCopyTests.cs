using System.Linq.Expressions;
using System.Reflection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Guards the copy constructor's one real risk: a property added later and not copied. Nothing
/// about that failure is visible — the copying form silently gets the library default where it
/// asked for the app-wide value — so the guard is a completeness check rather than a set of
/// per-property tests, and its property list is REFLECTED rather than written down. A written list
/// would drift exactly as the constructor does, which is the drift it exists to catch.
/// </summary>
public class FormidableOptionsCopyTests
{
    /// <summary>
    /// Every public settable property, set to a value distinguishable from its default, must
    /// survive the copy.
    /// <para>
    /// How it fails when someone adds a property and forgets to copy it: the reflected list grows
    /// by one, <see cref="DistinctValueFor"/> gives the new property a value the default is not,
    /// the copy constructor leaves it at that default, and the comparison at the end names it.
    /// A property whose type <see cref="DistinctValueFor"/> does not know fails there instead,
    /// with the same instruction — because a property nothing can set to a distinguishable value
    /// would pass this test vacuously, which is worse than failing it.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_settable_property_survives_the_copy()
    {
        var properties = SettableProperties();

        // A guard on the guard: an empty or near-empty reflection result would make everything
        // below pass without testing anything.
        Assert.True(
            properties.Count > 15,
            $"Only {properties.Count} settable properties were reflected — the discovery filter is wrong.");

        var library = new FormidableOptions();
        var source = new FormidableOptions();
        var vacuous = new List<string>();

        foreach (var property in properties)
        {
            var distinct = DistinctValueFor(property, property.GetValue(library));
            if (Equals(distinct, property.GetValue(library)))
            {
                vacuous.Add(property.Name);
                continue;
            }

            property.SetValue(source, distinct);
        }

        Assert.True(
            vacuous.Count == 0,
            "These properties were handed their own default, so the copy of them proves nothing: "
                + string.Join(", ", vacuous));

        var copy = new FormidableOptions(source);

        var uncopied = properties
            .Where(property => !Matches(property.GetValue(source), property.GetValue(copy)))
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            uncopied.Count == 0,
            "FormidableOptions' copy constructor does not carry these properties over, so a form "
                + "copying the app-wide defaults silently gets the library default for each — add "
                + "an assignment for every one: "
                + string.Join(", ", uncopied));
    }

    /// <summary>
    /// The shape the docs and the constructor's own XML teach, compiled and run rather than
    /// transcribed: the object initializer states the one setting that differs, and everything
    /// else comes across.
    /// </summary>
    [Fact]
    public void The_taught_shape_narrows_one_setting_and_keeps_the_rest()
    {
        var appWide = new FormidableOptions
        {
            RefreshDebounce = TimeSpan.FromMilliseconds(500),
            CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" },
        };

        var perForm = new FormidableOptions(appWide) { LiveProfile = ValidationProfile.Draft };

        Assert.Equal(ValidationProfile.Draft, perForm.LiveProfile);
        Assert.Equal(TimeSpan.FromMilliseconds(500), perForm.RefreshDebounce);
        Assert.Equal("is-invalid", perForm.CssClasses.Invalid);
        Assert.Equal("is-valid", perForm.CssClasses.Valid);
    }

    /// <summary>
    /// The class map is SHARED rather than cloned, and that is a decision rather than an
    /// oversight: it is read at each class computation so a replacement reaches kit and native
    /// inputs alike, and a copy that forked it would split the copying form off from every other
    /// form resolving the same app-wide map.
    /// </summary>
    [Fact]
    public void A_copy_shares_the_class_map_rather_than_cloning_it()
    {
        var appWide = new FormidableOptions();
        var copy = new FormidableOptions(appWide);

        Assert.Same(appWide.CssClasses, copy.CssClasses);

        appWide.CssClasses.Invalid = "is-invalid";

        Assert.Equal("is-invalid", copy.CssClasses.Invalid);
    }

    /// <summary>
    /// The other half of the sharing contract: what is copied is the property's VALUE, so
    /// assigning a different map to the source afterwards leaves the copy holding the one it was
    /// handed. Same snapshot rule as every other property; the map only looks different because
    /// the object behind the reference is itself mutable.
    /// </summary>
    [Fact]
    public void A_copy_keeps_the_class_map_it_was_handed_when_the_source_is_given_another()
    {
        var appWide = new FormidableOptions();
        var original = appWide.CssClasses;
        var copy = new FormidableOptions(appWide);

        appWide.CssClasses = new FormidableCssClasses { Invalid = "is-invalid" };

        Assert.Same(original, copy.CssClasses);
        Assert.Equal("formidable-invalid", copy.CssClasses.Invalid);
    }

    /// <summary>
    /// Adding the copy constructor must not take the parameterless one away — every existing
    /// `new FormidableOptions()`, the resolution fallback included, depends on it.
    /// </summary>
    [Fact]
    public void The_parameterless_constructor_still_builds_the_defaults()
    {
        var options = new FormidableOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(300), options.RefreshDebounce);
        Assert.Null(options.LiveProfile);
        Assert.Equal(ValidationProfile.Submit, options.SubmitProfile);
        Assert.True(options.ShowRequiredIndicators);
    }

    [Fact]
    public void Copying_nothing_throws_rather_than_producing_defaults()
    {
        Assert.Throws<ArgumentNullException>(() => new FormidableOptions(null!));
    }

    private static IReadOnlyList<PropertyInfo> SettableProperties() =>
        [.. typeof(FormidableOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetMethod!.IsPublic && p.SetMethod!.IsPublic)
            .OrderBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>
    /// A value for <paramref name="property"/> that its default is not. Every shape the type
    /// carries is handled explicitly; an unrecognised one throws rather than returning something
    /// that might coincide with the default, because a property set to its own default proves
    /// nothing about whether the copy constructor touched it.
    /// </summary>
    private static object? DistinctValueFor(PropertyInfo property, object? current)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
        {
            return !(current as bool? ?? false);
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        }

        if (type == typeof(TimeSpan))
        {
            // Far from every debounce default, and different per property so two properties
            // cannot pass by holding each other's value.
            return TimeSpan.FromMilliseconds(4000 + property.Name.Length);
        }

        if (type == typeof(string))
        {
            return $"copy-probe-{property.Name}";
        }

        if (type == typeof(ValidationProfile))
        {
            return ValidationProfile.Named($"CopyProbe{property.Name}");
        }

        if (type == typeof(FormidableCssClasses))
        {
            return new FormidableCssClasses { Invalid = $"copy-probe-{property.Name}" };
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return NoOpDelegate(type);
        }

        throw new NotSupportedException(
            $"{property.Name} is a {property.PropertyType} and this test does not know how to give "
                + "that type a value its default is not. Add a branch for it — leaving it out would "
                + "let the copy constructor drop the property without the test noticing.");
    }

    /// <summary>
    /// A fresh delegate of the exact type asked for, whatever its signature: parameters ignored,
    /// the return value <c>default</c>. Built rather than hand-written so a delegate-typed
    /// property added later needs no branch of its own — and a fresh instance every call, which is
    /// what makes it distinguishable from the <see langword="null"/> every delegate here defaults
    /// to.
    /// </summary>
    private static Delegate NoOpDelegate(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name))
            .ToArray();
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    // Reference-typed values are carried across by reference — the copy holds the same string,
    // profile, delegate or class map the source does — so identity is the sharper comparison and
    // it is the one the copy constructor actually promises.
    private static bool Matches(object? expected, object? actual) =>
        expected is null || expected.GetType().IsValueType
            ? Equals(expected, actual)
            : ReferenceEquals(expected, actual);
}
