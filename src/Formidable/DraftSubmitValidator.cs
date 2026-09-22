namespace Formidable;

/// <summary>
/// Convenience base for the common save-draft/submit form lifecycle: draft rules answer
/// "is the value malformed / out of range?" and treat default values (empty string, 0, null)
/// as valid; submit rules answer "is the value present?" and treat default values as missing.
/// Keeping the axes orthogonal avoids double messages for a single mistake. Draft rules run
/// under <see cref="ValidationProfile.Draft"/> (and any profile including default rules);
/// submit rules run under <see cref="ValidationProfile.Submit"/>. Additional profiles beyond
/// the lifecycle pair register in <see cref="ConfigureAdditionalProfiles"/>.
/// This class is one packaged convention over <see cref="ProfiledValidator{T}"/> — validators
/// with a different profile shape derive from <see cref="ProfiledValidator{T}"/> directly.
/// </summary>
/// <remarks>
/// The configure hooks run from the base constructor — see the remarks on
/// <see cref="ProfiledValidator{T}"/> about derived constructor state.
/// </remarks>
public abstract class DraftSubmitValidator<T> : ProfiledValidator<T>
{
    /// <summary>Routes common rules to <see cref="ConfigureDraftRules"/>.</summary>
    protected sealed override void ConfigureCommonRules() => ConfigureDraftRules();

    /// <summary>Registers the submit ruleset, any additional profiles, then checks for overlapping axes.</summary>
    protected sealed override void ConfigureProfiles()
    {
        Profile(ValidationProfile.SubmitRuleSetName, ConfigureSubmitRules);
        ConfigureAdditionalProfiles();
        ReportOverlappingRuleAxes();
    }

    /// <summary>Format/length/range rules. Default values are valid — presence belongs in submit rules.</summary>
    protected abstract void ConfigureDraftRules();

    /// <summary>Required-field, cross-field, and business rules. Default values are missing.</summary>
    protected abstract void ConfigureSubmitRules();

    /// <summary>Optional: register rulesets for profiles beyond the draft/submit pair via <see cref="ProfiledValidator{T}.Profile(string, Action)"/>.</summary>
    protected virtual void ConfigureAdditionalProfiles()
    {
    }

    /// <summary>
    /// Invoked once per (property, validator) pair that appears in BOTH the draft (default)
    /// rules and the submit ruleset — usually a sign the orthogonal-concerns convention was
    /// broken and the user will see two messages for one mistake. Default: a Debug-output
    /// warning. Override to route elsewhere or suppress. Child/collection rule contents are
    /// not inspected — the diagnostic covers leaf property validators only.
    /// </summary>
    /// <remarks>
    /// Invoked from the base constructor; derived constructor-body state is not yet
    /// initialized when an override runs (field initializers are safe) — see the remarks on
    /// <see cref="ProfiledValidator{T}"/>.
    /// </remarks>
    protected virtual void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
        System.Diagnostics.Debug.WriteLine(
            $"Formidable: '{propertyName}' has {validatorName} rules in both the draft and submit axes; " +
            "draft handles malformed-ness, submit handles presence - overlapping rules produce double messages.");

    private void ReportOverlappingRuleAxes()
    {
        // AbstractValidator<T> enumerates its rules; each IValidationRule carries the property
        // name, the rulesets it was registered under, and its component validators.
        var draftAxis = new HashSet<(string Property, string Validator)>();
        var submitAxis = new HashSet<(string Property, string Validator)>();

        foreach (var rule in (IEnumerable<FluentValidation.IValidationRule>)this)
        {
            if (string.IsNullOrEmpty(rule.PropertyName))
            {
                continue; // model-level rules have no single property axis
            }

            var ruleSets = rule.RuleSets ?? [];
            var isDraft = ruleSets.Length == 0;
            var isSubmit = ruleSets.Contains(ValidationProfile.SubmitRuleSetName);

            foreach (var component in rule.Components)
            {
                if (component.Validator is FluentValidation.Validators.IChildValidatorAdaptor)
                {
                    continue; // Child/collection rule wrappers share one adaptor type regardless of
                              // their inner rules — comparing them by type name would false-positive.
                }

                var entry = (rule.PropertyName, component.Validator.GetType().Name);
                if (isDraft)
                {
                    draftAxis.Add(entry);
                }

                if (isSubmit)
                {
                    submitAxis.Add(entry);
                }
            }
        }

        foreach (var (property, validator) in draftAxis.Intersect(submitAxis))
        {
            OnOverlappingRuleAxes(property, validator);
        }
    }
}
