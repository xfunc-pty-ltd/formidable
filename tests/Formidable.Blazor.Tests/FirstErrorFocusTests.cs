using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The diagnostic a focus move writes when it finds no element and no <c>FocusFallback</c> is
/// wired. A field's names the field and the fallback that would recover it. The form's own
/// message has no field to recover, so its diagnostic names the form and the attributes the
/// form needs instead. The move is driven through <see cref="FirstErrorFocus.MoveAsync"/> with a
/// focus service that never lands, and the warning is read from a captured logger and from Trace.
/// </summary>
public class FirstErrorFocusTests
{
    // The first issue to show is the form's own message: the server replied with an issue on the
    // empty path, which the engine files under the model-level field. The move aims at the form,
    // misses, and the warning has to say what a form needs to take focus. FocusFallback is the
    // wrong advice there, since the callback would be handed the model-level field and the
    // usual cause is the missing id and tabindex.
    // Mutation: drop the empty-path branch from ReportFallbackMiss, so the field sentence is
    // written for the form and the "tabindex" assertion fails. Mutation: keep the branch but name
    // the fallback in it, so the last assertion fails.
    [Fact]
    public async Task Warning_for_the_model_level_field_names_the_form()
    {
        var order = new EngineOrder();
        using var engine = NewEngine(order);
        engine.ApplyServerIssues([new ValidationIssue(string.Empty, "The order could not be saved.")]);

        var (logged, traceLines, focus) = await MissAsync(engine);

        // The move did aim at the form: the one request names the model-level field.
        Assert.Equal(string.Empty, Assert.Single(focus.Requests).FieldName);

        var warning = Assert.Single(logged, entry => entry.Level == LogLevel.Warning).Message;
        Assert.Contains("form", warning);
        Assert.Contains("the form's own message", warning);
        Assert.Contains("tabindex=\"-1\"", warning);
        Assert.Contains("did not take focus", warning);
        Assert.DoesNotContain("FocusFallback", warning);

        // The Trace channel carries the same text.
        var traced = Assert.Single(traceLines.Where(line => line.Contains("tabindex=\"-1\"")).Distinct());
        Assert.Contains("form", traced);
        Assert.DoesNotContain("FocusFallback", traced);
    }

    // A field on screen that will not take focus keeps the sentence that names it and the
    // fallback that would recover it, and says nothing of the form.
    // Mutation: write the form sentence for every miss, so the fallback assertion fails.
    [Fact]
    public async Task Warning_for_a_field_keeps_the_fallback_sentence()
    {
        var order = new EngineOrder();
        using var engine = NewEngine(order);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var onScreen = engine.Registry.Register(description);
        engine.ApplyServerIssues([new ValidationIssue(nameof(EngineOrder.Description), "server said no")]);

        var (logged, traceLines, focus) = await MissAsync(engine);

        Assert.Equal(description, Assert.Single(focus.Requests));

        var warning = Assert.Single(logged, entry => entry.Level == LogLevel.Warning).Message;
        Assert.Contains($"'{nameof(EngineOrder.Description)}'", warning);
        Assert.Contains("did not take focus", warning);
        Assert.Contains("no FocusFallback is wired", warning);
        Assert.DoesNotContain("tabindex", warning);

        // The field's own name is part of the match: another class running in parallel can write
        // the same sentence for a different field to the same process-wide listener.
        var traced = Assert.Single(
            traceLines
                .Where(line => line.Contains("no FocusFallback is wired")
                    && line.Contains($"'{nameof(EngineOrder.Description)}'"))
                .Distinct());
        Assert.Contains("did not take focus", traced);
    }

    private static FormidableEngine<EngineOrder> NewEngine(EngineOrder order) =>
        new(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

    /// <summary>
    /// Runs one move with no fallback and no prepare hook against a focus service that never
    /// lands, and returns what the logger and Trace caught beside the service.
    /// </summary>
    private static async Task<(IReadOnlyList<(LogLevel Level, string Message)> Logged, List<string> Trace, RecordingFocusService Focus)> MissAsync(
        FormidableEngine<EngineOrder> engine)
    {
        var focus = new RecordingFocusService { Lands = false };
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        using var services = new ServiceCollection()
            .AddSingleton<IFormidableFocusService>(focus)
            .AddSingleton<ILoggerFactory>(loggerFactory)
            .BuildServiceProvider();

        // Every Trace line is kept and the assertions match by content: the suite runs other
        // classes concurrently, and any of them can write to the same process-wide listener.
        var traceLines = await TraceCapture.RunAsync(
            () => FirstErrorFocus.MoveAsync(services, engine, fallback: null, prepare: null).AsTask());

        return (loggerProvider.Entries, traceLines, focus);
    }
}
