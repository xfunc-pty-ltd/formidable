using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The one-time note a read of <see cref="IFormidableEngine.IsFormValid"/> writes while
/// <see cref="FormidableOptions.TrackFormValidity"/> is off. The Trace listener is process-wide,
/// so every capture is filtered to lines naming <see cref="UntrackedValidityModel"/>, a model no
/// other test class uses: a parallel class writing its own diagnostics cannot add a line here.
/// </summary>
public class UntrackedFormValidityDiagnosticTests : BunitContext
{
    // Mutation: drop the once-latch, so every untracked read writes the note. Two reads then give
    // two Trace lines and two warnings.
    [Fact]
    public void Reading_IsFormValid_untracked_writes_one_note_per_engine()
    {
        var logger = new CapturingLogger();

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(new FormidableOptions(), logger);

            // Once through the class and once through the interface a component reads.
            _ = engine.IsFormValid;
            _ = ((IFormidableEngine)engine).IsFormValid;
        }, IsNote);

        var line = Assert.Single(lines);
        Assert.Contains("FormidableOptions.TrackFormValidity", line);
        Assert.Contains("keeps its last answer", line);

        var warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("IsFormValid"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(UntrackedValidityModel), warning.Message);
        Assert.Contains("FormidableOptions.TrackFormValidity", warning.Message);
        Assert.Contains("keeps its last answer", warning.Message);
    }

    // Mutation: drop the option test from the getter, so a tracked read writes the note too.
    [Fact]
    public void Reading_IsFormValid_tracked_writes_nothing()
    {
        var logger = new CapturingLogger();

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(new FormidableOptions { TrackFormValidity = true }, logger);
            _ = engine.IsFormValid;
            _ = engine.IsFormValid;
        }, IsNote);

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("IsFormValid"));
    }

    // Mutation: read the public IsFormValid in AdoptFormValidity ahead of its option test, so a
    // submit or a load with tracking off writes the note though the page never read it.
    [Fact]
    public async Task The_engine_s_own_reads_write_nothing()
    {
        var logger = new CapturingLogger();

        var lines = await TraceCapture.RunAsync(async () =>
        {
            using var engine = BuildEngine(new FormidableOptions { DisclosureOverride = _ => true }, logger);
            await engine.ValidateForSubmitAsync();
            await engine.DiscloseLoadedValuesAsync();
        }, IsNote);

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("IsFormValid"));
    }

    // The one internal read that can happen with tracking off: a validity check started while
    // tracking was on, answering after the page turned it off. Its answer is written through the
    // backing field, so it notes nothing.
    // Mutation: route SetFormValidity's comparison through the public getter, so the landing
    // writes the note.
    [Fact]
    public async Task A_validity_check_answering_after_tracking_is_turned_off_writes_nothing()
    {
        var logger = new CapturingLogger();
        var validator = new GatedUntrackedValidityValidator();
        var options = new FormidableOptions { TrackFormValidity = true };

        var lines = await TraceCapture.RunAsync(async () =>
        {
            // Construction starts the first validity check, which waits on the gate.
            using var engine = BuildEngine(options, logger, validator);
            options.TrackFormValidity = false;

            var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.StateChanged += (_, _) => landed.TrySetResult();

            // The gated rule passes, so the answer flips from false and the landing notifies.
            validator.Gate.SetResult();
            await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }, IsNote);

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("IsFormValid"));
    }

    // Mutation: make the once-latch static (one note per process and model type rather than per
    // engine), so the engine ResetAsync builds stays silent.
    [Fact]
    public async Task A_new_engine_notes_again()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        Services.AddFormidableBlazor();
        Services.AddSingleton<IValidator<UntrackedValidityModel>, UntrackedValidityValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<UntrackedValidityModel>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<UntrackedValidityModel>.Model), new UntrackedValidityModel());
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<UntrackedValidityModel>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => _ => { }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<UntrackedValidityModel>>();

        var first = cut.Instance.Engine!;
        IFormidableEngine? second = null;

        var lines = await TraceCapture.RunAsync(async () =>
        {
            _ = first.IsFormValid;
            _ = first.IsFormValid;

            await cut.InvokeAsync(() => cut.Instance.ResetAsync());
            second = cut.Instance.Engine!;
            _ = second.IsFormValid;
            _ = second.IsFormValid;
        }, IsNote);

        Assert.NotSame(first, second);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2, loggerProvider.Entries.Count(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("IsFormValid")));

        await Services.DisposeAsync();
    }

    private static FormidableEngine<UntrackedValidityModel> BuildEngine(
        FormidableOptions options,
        ILogger logger,
        IValidator<UntrackedValidityModel>? validator = null)
    {
        var model = new UntrackedValidityModel();
        return new FormidableEngine<UntrackedValidityModel>(
            model,
            new EditContext(model),
            new FluentValidationModelValidator<UntrackedValidityModel>(validator ?? new UntrackedValidityValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider(),
            logger: logger);
    }

    /// <summary>Whether a Trace line is the note, written for this class's model.</summary>
    private static bool IsNote(string line) =>
        line.Contains(nameof(UntrackedValidityModel)) && line.Contains("IsFormValid");

    public sealed class UntrackedValidityModel
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class UntrackedValidityValidator : AbstractValidator<UntrackedValidityModel>
    {
        public UntrackedValidityValidator() => RuleFor(x => x.Name).NotEmpty();
    }

    /// <summary>One async rule that waits on <see cref="Gate"/> and then passes.</summary>
    public sealed class GatedUntrackedValidityValidator : AbstractValidator<UntrackedValidityModel>
    {
        public GatedUntrackedValidityValidator() =>
            RuleFor(x => x.Name).MustAsync(async (_, ct) =>
            {
                await Gate.Task.WaitAsync(ct);
                return true;
            });

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
