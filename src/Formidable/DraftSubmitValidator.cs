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

    /// <summary>Registers the submit ruleset, then any additional profiles.</summary>
    protected sealed override void ConfigureProfiles()
    {
        Profile(ValidationProfile.SubmitRuleSetName, ConfigureSubmitRules);
        ConfigureAdditionalProfiles();
    }

    /// <summary>Format/length/range rules. Default values are valid — presence belongs in submit rules.</summary>
    protected abstract void ConfigureDraftRules();

    /// <summary>Required-field, cross-field, and business rules. Default values are missing.</summary>
    protected abstract void ConfigureSubmitRules();

    /// <summary>Optional: register rulesets for profiles beyond the draft/submit pair via <see cref="ProfiledValidator{T}.Profile(string, Action)"/>.</summary>
    protected virtual void ConfigureAdditionalProfiles()
    {
    }
}
