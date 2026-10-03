using System.Reflection;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;

namespace Formidable;

/// <summary>Checks once per process that the loaded FluentValidation assembly carries the five members rule inspection reads by name, and keeps them for the reading.</summary>
/// <remarks>
/// The members are <c>ChildValidatorAdaptor&lt;T, TProperty&gt;</c>'s <c>GetValidator</c> and
/// <c>RuleSets</c>, <c>ICollectionRule&lt;T, TElement&gt;</c>'s <c>Filter</c> and
/// <c>AsyncFilter</c>, and <c>RuleComponent&lt;T, TProperty&gt;</c>'s <c>SeverityProvider</c>.
/// A missing one makes <see cref="IRuleInspectingValidator{TModel}.CanInspectRules"/> answer
/// <see langword="false"/> and writes one Trace line naming the cause, instead of letting a lost
/// by-name read degrade a requirement answer silently.
/// </remarks>
// The compile-bound FluentValidation surface fails loudly when a release removes what it binds
// to; these five reads fail quietly, each degrading its own answer (a lost row-filter read turns
// "conditionally required" into a flat "required", and a lost severity read turns a warning's
// "not required" back into "required"), so a FluentValidation resolved above the range this
// package declares could put wrong requiredness claims on a form with no signal anywhere. This
// check turns that silence into an honest "cannot tell".
internal static class FluentValidationInspectionSurface
{
    // The five by-name reads, named once here. The walk (FluentValidationModelValidator.Inspection.cs)
    // reads the adaptor's two and the severity through Members and the row filter's two by these
    // same names, so the guard and the walk cannot drift to different members.
    private const string GetValidatorMethod = "GetValidator";
    private const string RuleSetsProperty = "RuleSets";
    internal const string FilterProperty = "Filter";
    internal const string AsyncFilterProperty = "AsyncFilter";
    private const string SeverityProviderProperty = "SeverityProvider";

    // Lazy, so the reflection runs on the first inspection ask rather than at type load, and
    // in its default ExecutionAndPublication mode, so the check runs once per process and the
    // diagnostic inside it cannot repeat.
    private static readonly Lazy<InspectionMembers?> Found = new(Verify);

    /// <summary>The members the check found, each declared on its open generic type; <see langword="null"/> when any one is missing.</summary>
    internal static InspectionMembers? Members => Found.Value;

    /// <summary>Whether every member the inspection walk reads by name was found.</summary>
    internal static bool Intact => Members is not null;

    /// <summary>The five members, as found on the open generic types the walk closes.</summary>
    /// <param name="GetValidator"><c>ChildValidatorAdaptor&lt;T, TProperty&gt;.GetValidator</c>.</param>
    /// <param name="RuleSets"><c>ChildValidatorAdaptor&lt;T, TProperty&gt;.RuleSets</c>.</param>
    /// <param name="Filter"><c>ICollectionRule&lt;T, TElement&gt;.Filter</c>.</param>
    /// <param name="AsyncFilter"><c>ICollectionRule&lt;T, TElement&gt;.AsyncFilter</c>.</param>
    /// <param name="SeverityProvider"><c>RuleComponent&lt;T, TProperty&gt;.SeverityProvider</c>.</param>
    internal sealed record InspectionMembers(
        MethodInfo GetValidator,
        PropertyInfo RuleSets,
        PropertyInfo Filter,
        PropertyInfo AsyncFilter,
        PropertyInfo SeverityProvider);

    /// <summary>Looks each by-name member up on the open generic type the walk closes, counting a lookup that throws as not found.</summary>
    /// <returns>The five members when all were found; otherwise <see langword="null"/>.</returns>
    // Each lookup names its open type with a literal typeof, which is what keeps the trimmer from
    // removing the member. A member the reflection cannot single out is one the walk cannot read,
    // whatever the reason.
    internal static InspectionMembers? Verify()
    {
        InspectionMembers? members;
        try
        {
            members =
                typeof(ChildValidatorAdaptor<,>).GetMethod(GetValidatorMethod, BindingFlags.Public | BindingFlags.Instance) is { } getValidator
                && typeof(ChildValidatorAdaptor<,>).GetProperty(RuleSetsProperty, BindingFlags.Public | BindingFlags.Instance) is { } ruleSets
                && typeof(ICollectionRule<,>).GetProperty(FilterProperty, BindingFlags.Public | BindingFlags.Instance) is { } filter
                && typeof(ICollectionRule<,>).GetProperty(AsyncFilterProperty, BindingFlags.Public | BindingFlags.Instance) is { } asyncFilter
                && typeof(RuleComponent<,>).GetProperty(SeverityProviderProperty, BindingFlags.Public | BindingFlags.Instance) is { } severityProvider
                    ? new InspectionMembers(getValidator, ruleSets, filter, asyncFilter, severityProvider)
                    : null;
        }
        catch (Exception)
        {
            members = null;
        }

        if (members is null)
        {
            System.Diagnostics.Trace.WriteLine(
                "Formidable: the resolved FluentValidation assembly is missing members rule inspection " +
                "reads by name (ChildValidatorAdaptor<,>.GetValidator/.RuleSets, " +
                "ICollectionRule<,>.Filter/.AsyncFilter, RuleComponent<,>.SeverityProvider). " +
                "CanInspectRules answers false and the " +
                "requirement and declared-path readers claim nothing; use a FluentValidation version " +
                "inside the range this Formidable release declares.");
        }

        return members;
    }
}
