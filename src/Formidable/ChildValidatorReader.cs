using System.Reflection;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;

namespace Formidable;

/// <summary>Reads the validator a child-validator component wraps, and the rulesets scoping it, without a model.</summary>
// The one read of a child validator. The rule-inspection walk reads every child it meets through
// it, and the empty-selection report reads each Include it meets, so the two agree on which
// children can be had without a model.
internal static class ChildValidatorReader
{
    // ValidationContext<T>'s one-argument constructor, found on the open type by a literal typeof
    // so the trimmer keeps it, and matched on each read to the closed context type GetValidator
    // takes. It is public FluentValidation API, not one of the by-name reads the surface check
    // vouches for, so its absence is tested here and leaves the child unread.
    private static readonly ConstructorInfo? OpenContextConstructor =
        typeof(ValidationContext<>).GetConstructor([typeof(ValidationContext<>).GetGenericArguments()[0]]);

    /// <summary>What one child-validator component yields: the validator it wraps, or <see langword="null"/> where that needs a model, and the adaptor's rulesets as declared.</summary>
    /// <param name="Validator">The wrapped validator, or <see langword="null"/> where it cannot be had without a model.</param>
    /// <param name="RuleSets">The rulesets the adaptor scopes the child with; <see langword="null"/> and empty both mean unscoped.</param>
    internal sealed record ChildReading(IValidator? Validator, string[]? RuleSets)
    {
        /// <summary>The reading of a component whose child cannot be had.</summary>
        public static readonly ChildReading Unreadable = new(null, null);
    }

    /// <summary>The validator a child component wraps and the rulesets scoping it; the validator is <see langword="null"/> where it needs a model.</summary>
    /// <param name="component">The child-validator component to read.</param>
    /// <param name="modelType">The type the component's rule judges.</param>
    /// <param name="propertyType">The type the child validator judges.</param>
    /// <returns>The reading; <see cref="ChildReading.Unreadable"/> where the child cannot be had.</returns>
    /// <remarks>
    /// A child supplied by a lambda has that lambda run with no model, so one that reads the
    /// model is unreadable, while one that ignores its arguments is read like any other child.
    /// </remarks>
    // FluentValidation exposes both through ChildValidatorAdaptor<T, TProperty>, the GetValidator
    // method and the public RuleSets property its published XML docs do not mention. The surface
    // check finds each once, on the open type named by a literal typeof (which is what keeps the
    // trimmer from removing it), and the reading matches it to the closed type the live adaptor
    // already has. The call needs a context, and there is no model, so it passes one carrying
    // none: an adaptor holding a validator instance ignores it and hands the validator back; one
    // holding a factory runs that factory against a model that is not there. Every failure lands
    // in the same place (no child) because both callers decorate or advise: a missing
    // requirement mark beats a thrown render, and an unwritten Trace line beats a failed call.
    // Every closed type this touches is read off the live adaptor rather than built from type
    // arguments. The pair check holds the adaptor to the rule it sits on: one closed over another
    // pair than that rule's model and property types is left unread, a missing answer rather than
    // a guess. A hand-built adaptor can fail the check, since component variance lets one written
    // for a base property type sit on a rule over a derived one.
    internal static ChildReading Read(IRuleComponent component, Type modelType, Type propertyType)
    {
        try
        {
            var adaptor = ClosedTypeOf(component.Validator, typeof(ChildValidatorAdaptor<,>));
            if (adaptor is null
                || adaptor.GetGenericArguments() is not [var judged, var child]
                || judged != modelType
                || child != propertyType)
            {
                return ChildReading.Unreadable;
            }

            // The inspection walk reads only while the surface is intact. The empty-selection
            // report reads without asking, so a missing member leaves its child unread here.
            if (FluentValidationInspectionSurface.Members is not { } members)
            {
                return ChildReading.Unreadable;
            }

            var getValidator = (MethodInfo)adaptor.GetMemberWithSameMetadataDefinitionAs(members.GetValidator);

            // GetValidator's first parameter is the context type it takes, ValidationContext<T>
            // for the adaptor's T; the open type's one-argument constructor is matched to that
            // closed one.
            var contextType = getValidator.GetParameters()[0].ParameterType;
            if (OpenContextConstructor is null)
            {
                return ChildReading.Unreadable;
            }

            var context = ((ConstructorInfo)contextType.GetMemberWithSameMetadataDefinitionAs(OpenContextConstructor)).Invoke([null]);

            if (getValidator.Invoke(component.Validator, [context, null]) is not IValidator validator)
            {
                return ChildReading.Unreadable;
            }

            var ruleSets = ((PropertyInfo)adaptor.GetMemberWithSameMetadataDefinitionAs(members.RuleSets))
                .GetValue(component.Validator) as string[];

            return new ChildReading(validator, ruleSets);
        }
        catch (Exception)
        {
            return ChildReading.Unreadable;
        }
    }

    /// <summary>The closed form of <paramref name="openType"/> in the instance's own type or one of its base types, or <see langword="null"/> when it derives from none.</summary>
    /// <param name="instance">The live object: a component's validator, or the component itself.</param>
    /// <param name="openType">The open generic class, named by a literal <c>typeof</c> at the call site.</param>
    /// <returns>The closed type, or <see langword="null"/>.</returns>
    // The base types are walked because both classes read this way are open to subclassing
    // (FluentValidation's own polymorphic child validator subclasses the adaptor, and its component
    // for a nullable struct subclasses the component), and a subclass is read through the members
    // of the class it derives from. Nothing is built: each type is compared to the open one.
    internal static Type? ClosedTypeOf(object instance, Type openType)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == openType)
            {
                return type;
            }
        }

        return null;
    }
}
