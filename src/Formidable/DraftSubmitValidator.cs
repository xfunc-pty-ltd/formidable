using FluentValidation.Internal;
// An alias above the namespace resolves without the file's other using directives, global ones
// included, so its type is written out in full.
using RuleAxis = System.Collections.Generic.Dictionary<
    (string Property, string Validator),
    System.Collections.Generic.List<FluentValidation.Internal.IRuleComponent>>;

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

    /// <summary>Called at most once per property and validator type in both the draft rules and the Submit ruleset, usually a sign one mistake will show two messages. <c>Must</c> and <c>MustAsync</c> rules also need matching messages.</summary>
    /// <param name="propertyName">The property both axes hold a rule for.</param>
    /// <param name="validatorName">The component validator's type name as the runtime reports it (<c>NotEmptyValidator`2</c>).</param>
    /// <remarks>
    /// Comparing two <c>Must</c> or <c>MustAsync</c> messages runs each <c>WithMessage</c> lambda
    /// at most once, at construction and with no model; one that cannot be read that way counts as
    /// matching. Runs from the base constructor, before a derived constructor body (field
    /// initializers have run). The default writes a <see cref="System.Diagnostics.Trace"/> line,
    /// which the shipped release build keeps; override it to report elsewhere or to stay silent.
    /// Child and collection rule contents are not inspected: the check covers leaf property
    /// validators only.
    /// </remarks>
    protected virtual void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: '{propertyName}' has {validatorName} rules in both the draft and submit axes; " +
            "draft handles malformed-ness, submit handles presence - overlapping rules produce double messages.");

    private void ReportOverlappingRuleAxes()
    {
        // AbstractValidator<T> enumerates its rules; each IValidationRule carries the property
        // name, the rulesets it was registered under, and its component validators.
        var rules = (IEnumerable<FluentValidation.IValidationRule>)this;

        // Phase one keys each axis by (property, validator type) alone. When no property holds a
        // rule of one validator type on both axes (types compare by name), the two sets do not
        // intersect and the check stops here, with no component collected and no message read.
        var draftKeys = new HashSet<(string Property, string Validator)>();
        var submitKeys = new HashSet<(string Property, string Validator)>();
        foreach (var rule in rules)
        {
            var (isDraft, isSubmit) = AxesOf(rule);
            if (!isDraft && !isSubmit)
            {
                continue;
            }

            foreach (var component in rule.Components)
            {
                if (KeyOf(rule, component) is not { } key)
                {
                    continue;
                }

                if (isDraft)
                {
                    draftKeys.Add(key);
                }

                if (isSubmit)
                {
                    submitKeys.Add(key);
                }
            }
        }

        draftKeys.IntersectWith(submitKeys);
        var sharedKeys = draftKeys;
        if (sharedKeys.Count == 0)
        {
            return;
        }

        // Phase two collects the components of the shared keys only, and compares their messages.
        var draftAxis = new RuleAxis();
        var submitAxis = new RuleAxis();
        foreach (var rule in rules)
        {
            var (isDraft, isSubmit) = AxesOf(rule);
            if (!isDraft && !isSubmit)
            {
                continue;
            }

            foreach (var component in rule.Components)
            {
                if (KeyOf(rule, component) is not { } key || !sharedKeys.Contains(key))
                {
                    continue;
                }

                if (isDraft)
                {
                    AddComponent(draftAxis, key, component);
                }

                if (isSubmit)
                {
                    AddComponent(submitAxis, key, component);
                }
            }
        }

        // One call per shared key, so two predicate pairs sharing a key report once.
        foreach (var (key, draftComponents) in draftAxis)
        {
            if (ShareAMessage(draftComponents, submitAxis[key]))
            {
                OnOverlappingRuleAxes(key.Property, key.Validator);
            }
        }
    }

    // The draft axis is the default rules (no ruleset); the submit axis is any rule registered
    // under the Submit ruleset. A model-level rule has no single property axis, so it is on neither.
    private static (bool IsDraft, bool IsSubmit) AxesOf(FluentValidation.IValidationRule rule)
    {
        if (string.IsNullOrEmpty(rule.PropertyName))
        {
            return (false, false);
        }

        var ruleSets = rule.RuleSets ?? [];
        return (ruleSets.Length == 0, ruleSets.Contains(ValidationProfile.SubmitRuleSetName));
    }

    // Child and collection rule wrappers share one adaptor type whatever their inner rules check,
    // so comparing them by type name would report a false overlap; they get no key.
    private static (string Property, string Validator)? KeyOf(
        FluentValidation.IValidationRule rule,
        IRuleComponent component) =>
        component.Validator is FluentValidation.Validators.IChildValidatorAdaptor
            ? null
            : (rule.PropertyName, component.Validator.GetType().Name);

    private static void AddComponent(
        RuleAxis axis,
        (string Property, string Validator) key,
        IRuleComponent component)
    {
        if (!axis.TryGetValue(key, out var components))
        {
            axis[key] = components = [];
        }

        components.Add(component);
    }

    // A null message key compares by type alone, so it matches anything on the other axis. The
    // first null, or the first submit message the draft side also holds, answers for the key, and
    // no message after it is read.
    private static bool ShareAMessage(
        List<IRuleComponent> draftComponents,
        List<IRuleComponent> submitComponents)
    {
        var draftMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in draftComponents)
        {
            if (MessageKey(component) is not { } message)
            {
                return true;
            }

            draftMessages.Add(message);
        }

        foreach (var component in submitComponents)
        {
            if (MessageKey(component) is not { } message || draftMessages.Contains(message))
            {
                return true;
            }
        }

        return false;
    }

    // Every Must rule shares one validator type whatever it checks, and every MustAsync rule
    // another, so a predicate is keyed on its unformatted message as well: the message is how two
    // checks of one type are told apart. The scan reads each message at most once, and reading it
    // runs a message lambda with no instance, so a lambda that reads the instance throws. That
    // component returns null and compares by type alone rather than failing construction. Every
    // other component returns null too.
    private static string? MessageKey(IRuleComponent component)
    {
        var type = component.Validator.GetType();
        if (!type.IsGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(FluentValidation.Validators.PredicateValidator<,>)
            && definition != typeof(FluentValidation.Validators.AsyncPredicateValidator<,>))
        {
            return null;
        }

        try
        {
            return component.GetUnformattedErrorMessage();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
