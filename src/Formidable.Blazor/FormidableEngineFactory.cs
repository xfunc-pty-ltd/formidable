using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>
/// Builds the engine both Formidable hosts own, and is the single home for the resolution
/// contract the component-kit reference documents: a parameter wins, then the container, then a
/// built-in default. <see cref="FormidableForm{TModel}"/> and <see cref="FormidableValidator{TModel}"/>
/// differ only in where their model and edit context come from, so resolution living here is what
/// keeps the contract — and every message a consumer meets when the container comes up short —
/// from drifting apart between them.
/// </summary>
internal static class FormidableEngineFactory
{
    /// <summary>
    /// Builds an engine for one model and edit-context pair, resolving from
    /// <paramref name="services"/> whatever the host was not handed as a parameter.
    /// </summary>
    internal static FormValidationEngine<TModel> Create<TModel>(
        TModel model,
        EditContext editContext,
        IServiceProvider services,
        IModelValidator<TModel>? validator,
        FormidableOptions? options,
        Func<Func<Task>, Task> renderDispatch)
        where TModel : class =>
        new(
            model,
            editContext,
            validator ?? ResolveValidator<TModel>(services),
            ResolveIntrospector(services),
            options ?? ResolveOptions(services),
            renderDispatch: renderDispatch,
            logger: ResolveLogger(services));

    /// <summary>
    /// Guards the half of the options contract a host cannot honour: the engine reads
    /// <c>Options</c> once, at construction, so a later reference change would otherwise take
    /// effect nowhere and say nothing. <paramref name="rebuildHint"/> names the host's own way to
    /// get a new engine, which is the one way new options do apply.
    /// </summary>
    internal static void VerifyOptionsUnchanged(
        string host,
        FormidableOptions? bound,
        FormidableOptions? current,
        string rebuildHint)
    {
        if (ReferenceEquals(bound, current))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{host}'s Options parameter changed after the engine was built, and a new FormidableOptions " +
            "instance cannot take effect on its own — Options is read once, when the engine is built. Build " +
            "FormidableOptions once and hold it in a field (see docs/options.md), mutate that instance's " +
            $"properties to change behaviour mid-form, or {rebuildHint}.");
    }

    private static IModelValidator<TModel> ResolveValidator<TModel>(IServiceProvider services)
        where TModel : class
    {
        IModelValidator<TModel>? resolved;
        try
        {
            resolved = (IModelValidator<TModel>?)services.GetService(typeof(IModelValidator<TModel>));
        }
        catch (InvalidOperationException ex)
            when (services.GetService(typeof(FluentValidation.IValidator<TModel>)) is null)
        {
            // The open-generic adapter is registered but the validator it wraps is not, so
            // resolving it throws during activation rather than returning null — the likeliest
            // first-run wiring mistake, reached through the path that reports it worst. The
            // container's own text names the adapter's internals instead of the missing
            // registration, and under WebAssembly's default trimming it degrades further to a bare
            // resource key. Formidable's own string is the only one trimming cannot take away, so
            // this state gets one; the container's exception is preserved underneath it.
            //
            // The filter is what keeps that story honest: an activation failure a registered
            // IValidator<TModel> contradicts has some other cause, the message here would be a lie
            // for it, and the container's own report of the real cause is the better one to let
            // through untouched.
            throw new InvalidOperationException(MissingFluentValidatorMessage.For(typeof(TModel)), ex);
        }

        return resolved ?? throw new InvalidOperationException(
            $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call " +
            "services.AddFormidableBlazor() and register the FluentValidation validator.");
    }

    private static IModelIntrospector ResolveIntrospector(IServiceProvider services) =>
        (IModelIntrospector?)services.GetService(typeof(IModelIntrospector))
            ?? throw new InvalidOperationException(
                "No IModelIntrospector is registered — call services.AddFormidableBlazor().");

    private static FormidableOptions ResolveOptions(IServiceProvider services) =>
        (FormidableOptions?)services.GetService(typeof(FormidableOptions)) ?? new FormidableOptions();

    /// <summary>
    /// Optional, unlike every other resolution above: a direct <see cref="FormValidationEngine{TModel}"/>
    /// construction (no container involved) must stay possible with no logging at all, and a
    /// consumer who never registered <see cref="ILoggerFactory"/> gets the engine's pre-existing
    /// diagnostics (Trace, the callback) exactly as before — logging is additive, never required.
    /// </summary>
    private static ILogger? ResolveLogger(IServiceProvider services) =>
        ((ILoggerFactory?)services.GetService(typeof(ILoggerFactory)))?.CreateLogger("Formidable");
}
