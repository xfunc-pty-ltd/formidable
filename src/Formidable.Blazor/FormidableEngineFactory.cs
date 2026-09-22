using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>Builds the engine for both roots, resolving what a root was not handed: a parameter wins, then the container, then a built-in default.</summary>
// FormidableForm<TModel> and FormidableValidator<TModel> differ only in where their model and
// edit context come from, so resolution living here is what keeps the contract, and every
// message a consumer meets when the container comes up short, from drifting apart between them.
internal static class FormidableEngineFactory
{
    /// <summary>Builds an engine for one model and edit context, resolving from <paramref name="services"/> whatever the root was not handed.</summary>
    /// <typeparam name="TModel">The form model type.</typeparam>
    /// <param name="model">The model the engine validates.</param>
    /// <param name="editContext">The edit context the engine writes messages to.</param>
    /// <param name="services">The root's provider, resolving the validator, the introspector, the options, a <see cref="TimeProvider"/> and a logger.</param>
    /// <param name="validator">The root's <c>Validator</c> parameter, or <see langword="null"/> to resolve one.</param>
    /// <param name="options">The root's <c>Options</c> parameter, or <see langword="null"/> to resolve the registered options or build defaults.</param>
    /// <param name="renderDispatch">Runs work on the renderer's dispatcher.</param>
    /// <returns>The engine, holding the options instance it was built with.</returns>
    /// <exception cref="InvalidOperationException">No validator was handed in and none resolves from <paramref name="services"/>, or no <see cref="IModelIntrospector"/> is registered there.</exception>
    internal static FormidableEngine<TModel> Create<TModel>(
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
            timeProvider: ResolveTimeProvider(services),
            renderDispatch: renderDispatch,
            logger: ResolveLogger(services));

    /// <summary>Throws when a root's <c>Options</c> parameter is not the instance it was when the engine was built, which read it once.</summary>
    /// <param name="host">The root's name, for the message.</param>
    /// <param name="bound">The root's <c>Options</c> parameter as read when the engine was built; <see langword="null"/> when none was set.</param>
    /// <param name="current">The root's <c>Options</c> parameter as it reads on this render.</param>
    /// <param name="rebuildHint">The root's own way to a new engine, which the message names as the one route new options take.</param>
    /// <exception cref="InvalidOperationException"><paramref name="current"/> is not the same instance as <paramref name="bound"/>.</exception>
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
            $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered in the " +
            "container this render is resolving from — call services.AddFormidableBlazor() and " +
            "register the FluentValidation validator there. A two-project Blazor Web App has " +
            "one container per project, and a page that prerenders or runs on the server's " +
            "circuit resolves from the server's, so register there too.");
    }

    private static IModelIntrospector ResolveIntrospector(IServiceProvider services) =>
        (IModelIntrospector?)services.GetService(typeof(IModelIntrospector))
            ?? throw new InvalidOperationException(
                "No IModelIntrospector is registered in the container this render is resolving " +
                "from — call services.AddFormidableBlazor(). A two-project Blazor Web App has " +
                "one container per project, and a page that prerenders or runs on the server's " +
                "circuit resolves from the server's, so register there too.");

    private static FormidableOptions ResolveOptions(IServiceProvider services) =>
        (FormidableOptions?)services.GetService(typeof(FormidableOptions)) ?? new FormidableOptions();

    /// <summary>The container's <see cref="TimeProvider"/>, which every clock the engine keeps then reads, or <see langword="null"/> for the engine's <see cref="TimeProvider.System"/> default.</summary>
    /// <param name="services">The root's provider.</param>
    /// <returns>The registered provider, or <see langword="null"/> when none is registered.</returns>
    // Resolving here is what makes the engine constructor's parameter reachable from the shipped
    // roots at all, so a container that registers a TimeProvider (a test's FakeTimeProvider)
    // drives both debounce timers, the pass timestamps and the held-coverage bound together.
    private static TimeProvider? ResolveTimeProvider(IServiceProvider services) =>
        (TimeProvider?)services.GetService(typeof(TimeProvider));

    /// <summary>A logger in the <c>Formidable</c> category from the container's <see cref="ILoggerFactory"/>, or <see langword="null"/> when none is registered.</summary>
    /// <param name="services">The provider to resolve the factory from.</param>
    /// <returns>The logger, or <see langword="null"/>; a null logger leaves the Trace diagnostics standing alone.</returns>
    // Logging is additive, never required: a direct FormidableEngine<TModel> construction with
    // no container must stay possible with no logging at all. Internal rather than private so
    // the component-side diagnostics resolve their logger through this one method and the
    // category name has a single home.
    internal static ILogger? ResolveLogger(IServiceProvider services) =>
        ((ILoggerFactory?)services.GetService(typeof(ILoggerFactory)))?.CreateLogger("Formidable");
}
