namespace Formidable;

/// <summary>Base validator for the save-draft and submit lifecycle: draft rules check a value's shape, submit rules add presence.</summary>
/// <typeparam name="T">The model type the validator accepts.</typeparam>
/// <remarks>
/// Draft rules check that a value is well formed and in range and treat a default value (empty
/// string, 0, <see langword="null"/>) as valid; submit rules check that a value is present and
/// treat a default value as missing, so one mistake produces one message. Draft rules run under
/// every profile that includes default rules, <see cref="ValidationProfile.Draft"/> among them;
/// submit rules run under <see cref="ValidationProfile.Submit"/>. The hooks run from the base
/// constructor, before a derived constructor body (see <see cref="ProfiledValidator{T}"/>).
/// </remarks>
// One packaged convention over ProfiledValidator<T>; a validator with a different profile shape
// derives from ProfiledValidator<T> directly.
public abstract class DraftSubmitValidator<T> : ProfiledValidator<T>
{
    /// <summary>Routes the common rules to <see cref="ConfigureDraftRules"/>.</summary>
    protected sealed override void ConfigureCommonRules() => ConfigureDraftRules();

    /// <summary>Registers the Submit ruleset, then any additional profiles, then reports overlapping rule axes.</summary>
    protected sealed override void ConfigureProfiles()
    {
        Profile(ValidationProfile.SubmitRuleSetName, ConfigureSubmitRules);
        ConfigureAdditionalProfiles();
        ReportOverlappingRuleAxes();
    }

    /// <summary>Format, length and range rules, which let a default value pass; presence belongs to the submit rules.</summary>
    protected abstract void ConfigureDraftRules();

    /// <summary>Presence, cross-field and business rules; a default value counts as missing here.</summary>
    protected abstract void ConfigureSubmitRules();

    /// <summary>Registers rulesets for profiles beyond Draft and Submit through <see cref="ProfiledValidator{T}.Profile(string, Action)"/>; does nothing by default.</summary>
    protected virtual void ConfigureAdditionalProfiles()
    {
    }

    /// <summary>Called once per property and validator type that appear in both the draft rules and the Submit ruleset, usually a sign one mistake will show two messages.</summary>
    /// <param name="propertyName">The property both axes hold a rule for.</param>
    /// <param name="validatorName">The component validator's type name as the runtime reports it (<c>NotEmptyValidator`2</c>).</param>
    /// <remarks>
    /// Runs from the base constructor, before a derived constructor body (field initializers have
    /// run). The default writes a Debug-output line in a debug build of this library and nothing
    /// in the release build the package ships; override it to report elsewhere or to stay silent.
    /// Child and collection rule contents are not inspected: the check covers leaf property
    /// validators only.
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
