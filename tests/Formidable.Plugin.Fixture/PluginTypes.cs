using FluentValidation;

namespace Formidable.Plugin.Fixture;

// The unload tests load this assembly by path into a collectible AssemblyLoadContext, so every type
// here belongs to that context and can go when it unloads. The test projects build it but never
// reference its types.

/// <summary>A model the plugin owns.</summary>
public sealed class PluginOrder
{
    /// <summary>A name of five characters at most.</summary>
    public string? Name { get; set; }
}

/// <summary>A plain validator for the plugin's own model.</summary>
public sealed class PluginOrderValidator : AbstractValidator<PluginOrder>
{
    /// <summary>Declares the one rule.</summary>
    public PluginOrderValidator() => RuleFor(order => order.Name).MaximumLength(5);
}

/// <summary>A plugin's validator for a model the host owns.</summary>
// Version stands in for a model in the host's contracts assembly. The default context loads it, so
// DraftSubmitValidator<Version> is a default-context type, and its statics outlive the plugin.
public sealed class HostModelValidator : DraftSubmitValidator<Version>
{
    /// <inheritdoc />
    protected override void ConfigureDraftRules() => RuleFor(version => version.Major).GreaterThanOrEqualTo(0);

    /// <inheritdoc />
    protected override void ConfigureSubmitRules() => RuleFor(version => version.Minor).GreaterThanOrEqualTo(0);
}

/// <summary>What the plugin does with Formidable when the host asks.</summary>
public static class PluginEntry
{
    /// <summary>Validates a plugin model under Submit through the FluentValidation adapter.</summary>
    /// <returns>Whether the model passed.</returns>
    public static bool ValidateUnderSubmit() =>
        new FluentValidationModelValidator<PluginOrder>(new PluginOrderValidator())
            .ValidateAsync(new PluginOrder { Name = "Ada" }, ValidationProfile.Submit)
            .GetAwaiter()
            .GetResult()
            .IsValid;
}
