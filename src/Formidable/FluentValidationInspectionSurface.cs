using System.Reflection;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;

namespace Formidable;

/// <summary>Checks once per process that the loaded FluentValidation assembly carries the four members rule inspection reads by name.</summary>
/// <remarks>
/// The members are <c>ChildValidatorAdaptor&lt;T, TProperty&gt;</c>'s <c>GetValidator</c> and
/// <c>RuleSets</c> and <c>ICollectionRule&lt;T, TElement&gt;</c>'s <c>Filter</c> and
/// <c>AsyncFilter</c>. A missing one makes
/// <see cref="IRuleInspectingValidator{TModel}.CanInspectRules"/> answer <see langword="false"/>
/// and writes one Trace line naming the cause, instead of letting a lost by-name read degrade a
/// requirement answer silently.
/// </remarks>
// The compile-bound FluentValidation surface fails loudly when a release removes what it binds
// to; these four reads fail quietly, each degrading its own answer (a lost row-filter read turns
// "conditionally required" into a flat "required"), so a FluentValidation resolved above the
// range this package declares could put wrong requiredness claims on a form with no signal
// anywhere. This check turns that silence into an honest "cannot tell".
internal static class FluentValidationInspectionSurface
{
    // The four by-name reads the inspection walk performs, named once here and shared with the
    // walk (FluentValidationModelValidator.Inspection.cs) so the guard and the walk cannot drift
    // to different names of the same member.
    internal const string GetValidatorMethod = "GetValidator";
    internal const string RuleSetsProperty = "RuleSets";
    internal const string FilterProperty = "Filter";
    internal const string AsyncFilterProperty = "AsyncFilter";

    // Lazy, so the reflection runs on the first inspection ask rather than at type load, and
    // in its default ExecutionAndPublication mode, so the check runs once per process and the
    // diagnostic inside it cannot repeat.
    private static readonly Lazy<bool> Verified = new(Verify);

    /// <summary>Whether every member the inspection walk reads by name was found.</summary>
    internal static bool Intact => Verified.Value;

    /// <summary>Looks each by-name member up on the open generic type the walk closes, counting a lookup that throws as not found.</summary>
    /// <returns><see langword="true"/> when all four members were found.</returns>
    // The same route the walk takes, so what this finds is what the walk will find; a member
    // the reflection cannot single out is one the walk cannot read, whatever the reason.
    internal static bool Verify()
    {
        bool intact;
        try
        {
            intact =
                typeof(ChildValidatorAdaptor<,>).GetMethod(GetValidatorMethod, BindingFlags.Public | BindingFlags.Instance) is not null
                && typeof(ChildValidatorAdaptor<,>).GetProperty(RuleSetsProperty, BindingFlags.Public | BindingFlags.Instance) is not null
                && typeof(ICollectionRule<,>).GetProperty(FilterProperty, BindingFlags.Public | BindingFlags.Instance) is not null
                && typeof(ICollectionRule<,>).GetProperty(AsyncFilterProperty, BindingFlags.Public | BindingFlags.Instance) is not null;
        }
        catch (Exception)
        {
            intact = false;
        }

        if (!intact)
        {
            System.Diagnostics.Trace.WriteLine(
                "Formidable: the resolved FluentValidation assembly is missing members rule inspection " +
                "reads by name (ChildValidatorAdaptor<,>.GetValidator/.RuleSets, " +
                "ICollectionRule<,>.Filter/.AsyncFilter). CanInspectRules answers false and the " +
                "requirement and declared-path readers claim nothing; use a FluentValidation version " +
                "inside the range this Formidable release declares.");
        }

        return intact;
    }
}
