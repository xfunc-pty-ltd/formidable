using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A field that leaves the page under <see cref="LiveIssueDisclosure.EngagedAndVisible"/> keeps its
/// live message while <see cref="FormidableOptions.DisclosureOverride"/> answers
/// <see langword="true"/> for one of its issues and that issue's path still names the field. A
/// row deleted on screen has a path naming another row, or none, so its message goes at once.
/// Under the default policy, and without such an answer, a field that leaves the page loses its
/// live message.
/// </summary>
/// <remarks>
/// Code sits in a section the page can collapse, and every Code rule run is counted. Secret is
/// required and renders nothing. The clock is a <see cref="FakeTimeProvider"/>, so the whole-form
/// re-check after a change runs only when a test moves the clock.
/// </remarks>
public class DisclosureOverrideDepartureTests : BunitContext
{
    private const string NameRequired = "Name is required";
    private const string CodeRequired = "Code is required";
    private const string CodeUnusual = "Code looks unusual";
    private const string SecretRequired = "Secret is required";
    private const string SkuRequired = "SKU is required";

    private static readonly string GateText = new FormidableOptions().DefensiveGateMessage;
    private static readonly TimeSpan PastRefresh = new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _clock = new();
    private readonly ShipmentValidator _validator = new();
    private int _codeAsks;
    private bool? _codeAnswer = true;

    public DisclosureOverrideDepartureTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IValidator<Shipment>>(_validator);
        Services.AddSingleton<IValidator<Manifest>>(new ManifestValidator());
        Services.AddSingleton<IFormidableFocusService>(new NoFocus());
    }

    // Code is emptied, so "Code is required" shows; the section collapses, the re-check runs, and
    // the section opens again. The message stays at every step, on every surface. Code's rule runs
    // once for the edit and once for each re-check, and the collapse and the return run nothing.
    // Entering and leaving Code with no change runs nothing.
    // Mutation that must break it: drop the keep from the departure prune, and the message goes
    // at the collapse.
    [Fact]
    public async Task Under_EngagedAndVisible_an_override_true_keeps_a_departing_fields_message()
    {
        var shipment = new Shipment();
        var cut = RenderHost(shipment, KeepCodeOptions(), summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Equal(1, _validator.CodeRuns);

        await ShowCodeAsync(cut, false);

        AssertCodeRequiredOnEverySurface(cut, code);
        Assert.Equal(1, _validator.CodeRuns);

        await RecheckAsync(cut);

        AssertCodeRequiredOnEverySurface(cut, code);
        Assert.Equal(2, _validator.CodeRuns);

        await ShowCodeAsync(cut, true);

        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        AssertCodeRequiredOnEverySurface(cut, code);
        var input = cut.Find("div[data-entry=Code] input");
        Assert.Contains("formidable-invalid", input.ClassList);
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
        Assert.Equal(2, _validator.CodeRuns);

        await RecheckAsync(cut);

        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Equal(3, _validator.CodeRuns);

        // Entered and left with nothing committed: the blur a kit input reports.
        await cut.InvokeAsync(() => Engine(cut).MarkTouched(code));
        await RecheckAsync(cut);

        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Equal(3, _validator.CodeRuns);

        await Services.DisposeAsync();
    }

    // A row the visitor deletes while it is on screen takes its message with it at once, override
    // or not: the issue's path now names the row that moved up into its place, or no row at all,
    // so it no longer describes the field that left. Row 1 is a middle row and row 2 the last. Mutation that must
    // break it: drop the path check from the keep, and the summary goes on listing the deleted
    // row's message.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_deleted_row_loses_its_message_at_once_even_under_an_override(int row)
    {
        var options = new FormidableOptions
        {
            LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible,
            DisclosureOverride = issue => issue.Path.StartsWith("Lines[", StringComparison.Ordinal) ? true : null,
        };
        var manifest = new Manifest();
        var cut = Render<ManifestHost>(parameters => parameters.Add(p => p.Manifest, manifest).Add(p => p.Options, options));
        var engine = cut.FindComponent<FormidableForm<Manifest>>().Instance.Engine!;
        var deleted = manifest.Lines[row];
        var deletedField = new FieldIdentifier(deleted, nameof(Line.Sku));

        await cut.InvokeAsync(() => cut.Find($"div[data-entry=row{row}] input").Change(string.Empty));
        await SettleAsync(cut, engine);
        Assert.Equal([SkuRequired], SummaryEntries(cut));

        manifest.Lines.RemoveAt(row);
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Manifest, manifest)));
        await SettleAsync(cut, engine);

        Assert.Empty(SummaryEntries(cut));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(engine.GetIssues(deletedField));
        Assert.Empty(engine.EditContext.GetValidationMessages(deletedField));

        await Services.DisposeAsync();
    }

    // Control: under the default policy the live channel never asks the override, and a field
    // that leaves the page loses its live message. Opening the section again shows nothing until
    // a change engages Code again. The rule runs as often as in the keep case: once for the edit
    // and once per re-check. Mutation that must break it: drop the policy test from the keep, and
    // the message stays after the collapse.
    [Fact]
    public async Task Under_the_default_policy_departure_is_unchanged()
    {
        var options = KeepCodeOptions();
        options.LiveDisclosure = LiveIssueDisclosure.Engaged;
        var shipment = new Shipment();
        var cut = RenderHost(shipment, options, summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));

        await ShowCodeAsync(cut, false);

        AssertNothingForCode(cut, code);
        Assert.Equal(1, _validator.CodeRuns);

        await RecheckAsync(cut);

        AssertNothingForCode(cut, code);
        Assert.Equal(2, _validator.CodeRuns);

        await ShowCodeAsync(cut, true);
        await RecheckAsync(cut);

        Assert.Empty(KitMessages(cut, "Code"));
        AssertNothingForCode(cut, code);
        Assert.Equal(3, _validator.CodeRuns);
        Assert.Equal(0, _codeAsks);

        await Services.DisposeAsync();
    }

    // Control: under EngagedAndVisible with no override, or one that answers only for another
    // field, Code's message goes with Code and does not come back with it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Under_EngagedAndVisible_without_an_override_true_departure_is_unchanged(bool overrideForName)
    {
        var options = new FormidableOptions { LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible };
        if (overrideForName)
        {
            options.DisclosureOverride = issue => issue.Path == nameof(Shipment.Name) ? true : null;
        }

        var shipment = new Shipment();
        var cut = RenderHost(shipment, options, summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));

        await ShowCodeAsync(cut, false);
        AssertNothingForCode(cut, code);

        await RecheckAsync(cut);
        AssertNothingForCode(cut, code);

        await ShowCodeAsync(cut, true);
        await RecheckAsync(cut);

        Assert.Empty(KitMessages(cut, "Code"));
        AssertNothingForCode(cut, code);

        await Services.DisposeAsync();
    }

    // The gate's override clause. A submit blocked by Secret, which renders nothing, shows the
    // form-level message; emptying Code shows "Code is required" and takes it down. The section
    // then collapses: the summary goes on listing Code's message, through the override, and that
    // listing is what explains the block, so the form-level message stays away through the
    // re-check and the return. Mutations that must break it: drop the override clause from
    // GateActive's live loop, or drop the keep from the departure prune, and the form-level
    // message comes back at the collapse.
    [Fact]
    public async Task A_kept_fields_live_error_keeps_the_gate_down()
    {
        var shipment = new Shipment { Secret = string.Empty };
        var cut = RenderHost(shipment, KeepCodeOptions(), summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, shipment));

        await EditAsync(cut, "Code", string.Empty);
        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Empty(ModelMessages(cut, shipment));

        await ShowCodeAsync(cut, false);

        Assert.Equal([CodeRequired], SummaryEntries(cut));
        Assert.Empty(ModelMessages(cut, shipment));
        Assert.Contains(Engine(cut).GetIssues(code), i => i.Message == CodeRequired);

        await RecheckAsync(cut);

        Assert.Equal([CodeRequired], SummaryEntries(cut));
        Assert.Empty(ModelMessages(cut, shipment));
        Assert.Contains(Engine(cut).GetIssues(code), i => i.Message == CodeRequired);

        await ShowCodeAsync(cut, true);

        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Empty(ModelMessages(cut, shipment));

        await Services.DisposeAsync();
    }

    // A kept field keeps its place in an open live window. Under a LiveDebounce, Code is emptied and
    // checked, then filled with "A1" and the section collapses before the window closes. The
    // window's check answers the value Code left with, so "Code is required" goes. Code's rule
    // runs once for the empty value and once for "A1". Mutation that must break it: let the
    // departure drop every departed field from the window, kept or not, and the message stays on
    // a field that now holds "A1".
    [Fact]
    public async Task A_kept_fields_open_window_checks_the_value_it_left_with()
    {
        var options = KeepCodeOptions();
        options.LiveDebounce = TimeSpan.FromMilliseconds(500);
        var shipment = new Shipment();
        var cut = RenderHost(shipment, options, summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        await AdvanceAsync(cut, TimeSpan.FromMilliseconds(501));
        Assert.Equal([CodeRequired], KitMessages(cut, "Code"));
        Assert.Equal(1, _validator.CodeRuns);

        await EditAsync(cut, "Code", "A1");
        await ShowCodeAsync(cut, false);
        await AdvanceAsync(cut, TimeSpan.FromMilliseconds(501));

        AssertNothingForCode(cut, code);
        Assert.Equal(2, _validator.CodeRuns);

        await Services.DisposeAsync();
    }

    // A field whose input waits for a submit shows no live message, so it has none to keep, and
    // its message does not appear anywhere as it leaves. Mutation that must break it: keep a
    // departed field on its filed result rather than on what it shows, and "Code is required"
    // appears the moment Code leaves.
    [Fact]
    public async Task A_field_waiting_for_a_submit_shows_nothing_as_it_leaves()
    {
        var shipment = new Shipment();
        var cut = Render<ShipmentHost>(parameters => parameters
            .Add(p => p.Shipment, shipment)
            .Add(p => p.Options, KeepCodeOptions())
            .Add(p => p.Summary, true)
            .Add(p => p.CodeWaitsForSubmit, true));
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        Assert.Empty(KitMessages(cut, "Code"));
        Assert.Empty(SummaryEntries(cut));

        await ShowCodeAsync(cut, false);
        AssertNothingForCode(cut, code);

        await ShowCodeAsync(cut, true);
        Assert.Empty(KitMessages(cut, "Code"));
        AssertNothingForCode(cut, code);

        await Services.DisposeAsync();
    }

    // The keep is decided again at each change to which fields are on screen. Code is kept after
    // the collapse; the override then stops answering true for it, and Name is hidden, so Code
    // leaves. The override answers true again, Name returns and is edited, and Code returns: none
    // of it brings Code's message back, because Code is no longer engaged. Mutation that must
    // break it: once kept, never ask again, and the edit to Name shows "Code is required" in the
    // summary and the store, and on Code when it returns.
    [Fact]
    public async Task A_field_the_override_stops_keeping_leaves_at_the_next_change()
    {
        var shipment = new Shipment();
        var cut = RenderHost(shipment, KeepCodeOptions(), summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", string.Empty);
        await ShowCodeAsync(cut, false);
        AssertCodeRequiredOnEverySurface(cut, code);

        _codeAnswer = null;
        await ShowNameAsync(cut, false);
        AssertNothingForCode(cut, code);

        _codeAnswer = true;
        await ShowNameAsync(cut, true);
        await EditAsync(cut, "Name", "Bea");

        AssertNothingForCode(cut, code);

        await ShowCodeAsync(cut, true);

        Assert.Empty(KitMessages(cut, "Code"));
        AssertNothingForCode(cut, code);

        await Services.DisposeAsync();
    }

    // A warning keeps its field as an error does: the override answers for an issue, whatever its
    // severity. Code is set to "A0", which only a warning rule flags; the section collapses, the
    // re-check runs and the section opens again, and the warning shows throughout. Mutation that
    // must break it: keep a field only on an error, and the warning goes at the collapse.
    [Fact]
    public async Task A_warning_the_override_shows_keeps_its_field_too()
    {
        var shipment = new Shipment();
        var cut = RenderHost(shipment, KeepCodeOptions(), summary: true);
        var code = FieldIdentifier.Create(() => shipment.Code);

        await EditAsync(cut, "Code", "A0");
        Assert.Equal([CodeUnusual], KitMessages(cut, "Code"));

        await ShowCodeAsync(cut, false);

        Assert.Equal([CodeUnusual], Engine(cut).GetIssues(code).Select(i => i.Message));
        Assert.Contains(Engine(cut).GetVisibleIssues(), v => v.Field.Equals(code) && v.Issue.Message == CodeUnusual);

        await RecheckAsync(cut);

        Assert.Equal([CodeUnusual], Engine(cut).GetIssues(code).Select(i => i.Message));

        await ShowCodeAsync(cut, true);

        Assert.Equal([CodeUnusual], KitMessages(cut, "Code"));

        await Services.DisposeAsync();
    }

    private FormidableOptions KeepCodeOptions() => new()
    {
        LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible,
        DisclosureOverride = issue =>
        {
            if (issue.Path == nameof(Shipment.Code))
            {
                _codeAsks++;
                return _codeAnswer;
            }

            return null;
        },
    };

    private static void AssertCodeRequiredOnEverySurface(IRenderedComponent<ShipmentHost> cut, FieldIdentifier code)
    {
        var engine = Engine(cut);
        Assert.Equal([CodeRequired], engine.GetIssues(code).Select(i => i.Message));
        Assert.Equal([CodeRequired], engine.EditContext.GetValidationMessages(code));
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(code) && v.Issue.Message == CodeRequired);
        Assert.Equal([CodeRequired], SummaryEntries(cut));
    }

    private static void AssertNothingForCode(IRenderedComponent<ShipmentHost> cut, FieldIdentifier code)
    {
        var engine = Engine(cut);
        Assert.Empty(engine.GetIssues(code));
        Assert.Empty(engine.EditContext.GetValidationMessages(code));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(code));
        Assert.Empty(SummaryEntries(cut));
    }

    private IRenderedComponent<ShipmentHost> RenderHost(Shipment shipment, FormidableOptions options, bool summary = false) =>
        Render<ShipmentHost>(parameters => parameters
            .Add(p => p.Shipment, shipment)
            .Add(p => p.Options, options)
            .Add(p => p.Summary, summary));

    // Waits for any check the step started, then lets a reconcile or a posted republish run: the
    // dispatcher runs one piece of work at a time, so the queued no-op completes after them.
    private static async Task SettleAsync(IRenderedComponent<ShipmentHost> cut)
    {
        cut.WaitForState(() => !Engine(cut).IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static async Task SettleAsync(IRenderedComponent<ManifestHost> cut, IFormidableEngine engine)
    {
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static async Task EditAsync(IRenderedComponent<ShipmentHost> cut, string entry, string value)
    {
        await cut.InvokeAsync(() => cut.Find($"div[data-entry={entry}] input").Change(value));
        await SettleAsync(cut);
    }

    private static async Task ShowCodeAsync(IRenderedComponent<ShipmentHost> cut, bool show)
    {
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowCode, show)));
        await SettleAsync(cut);
    }

    private static async Task ShowNameAsync(IRenderedComponent<ShipmentHost> cut, bool show)
    {
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowName, show)));
        await SettleAsync(cut);
    }

    private Task RecheckAsync(IRenderedComponent<ShipmentHost> cut) => AdvanceAsync(cut, PastRefresh);

    private async Task AdvanceAsync(IRenderedComponent<ShipmentHost> cut, TimeSpan by)
    {
        await cut.InvokeAsync(() => _clock.Advance(by));
        await SettleAsync(cut);
    }

    private static async Task<SubmitOutcome> SubmitAsync(IRenderedComponent<ShipmentHost> cut)
    {
        var outcome = await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Shipment>>().Instance.SubmitAsync());
        await SettleAsync(cut);
        return outcome;
    }

    private static IFormidableEngine Engine(IRenderedComponent<ShipmentHost> cut) =>
        cut.FindComponent<FormidableForm<Shipment>>().Instance.Engine!;

    private static List<string> ModelMessages(IRenderedComponent<ShipmentHost> cut, Shipment shipment)
    {
        var id = FormidableFieldId.MessagesFor(new FieldIdentifier(shipment, string.Empty));
        var list = Assert.Single(cut.FindAll("ul"), ul => ul.Id == id);
        return list.Children.Select(li => li.TextContent.Trim()).ToList();
    }

    private static List<string> KitMessages(IRenderedComponent<ShipmentHost> cut, string entry) =>
        cut.FindAll($"div[data-entry={entry}] li").Select(li => li.TextContent.Trim()).ToList();

    private static List<string> SummaryEntries<T>(IRenderedComponent<T> cut)
        where T : IComponent =>
        cut.FindAll("li.formidable-summary__item").Select(li => li.TextContent.Trim()).ToList();

    /// <summary>A name, a code in a section the page can collapse, and a secret no input shows.</summary>
    public sealed class Shipment
    {
        public string? Name { get; set; } = "Ada";

        public string? Code { get; set; } = "A1";

        public string? Secret { get; set; } = "s";
    }

    /// <summary>Requires all three, counts each run of Code's required rule, and warns on a Code of "A0".</summary>
    private sealed class ShipmentValidator : AbstractValidator<Shipment>
    {
        private int _codeRuns;

        public ShipmentValidator()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);
            RuleFor(m => m.Code)
                .Must(value =>
                {
                    Interlocked.Increment(ref _codeRuns);
                    return !string.IsNullOrEmpty(value);
                })
                .WithMessage(CodeRequired);
            RuleFor(m => m.Code).Must(value => value != "A0").WithSeverity(Severity.Warning).WithMessage(CodeUnusual);
            RuleFor(m => m.Secret).NotEmpty().WithMessage(SecretRequired);
        }

        public int CodeRuns => Volatile.Read(ref _codeRuns);
    }

    /// <summary>Three lines, each needing a SKU.</summary>
    public sealed class Manifest
    {
        public List<Line> Lines { get; set; } = [new Line { Sku = "a" }, new Line { Sku = "b" }, new Line { Sku = "c" }];
    }

    /// <summary>One line of a <see cref="Manifest"/>.</summary>
    public sealed class Line
    {
        public string Sku { get; set; } = string.Empty;
    }

    private sealed class ManifestValidator : AbstractValidator<Manifest>
    {
        public ManifestValidator() =>
            RuleForEach(m => m.Lines).ChildRules(line => line.RuleFor(l => l.Sku).NotEmpty().WithMessage(SkuRequired));
    }

    /// <summary>Takes no focus, so a blocked submit's focus move touches no script.</summary>
    private sealed class NoFocus : IFormidableFocusService
    {
        public ValueTask<bool> FocusAsync(FieldIdentifier field) => ValueTask.FromResult(false);
    }

    /// <summary>A form over <see cref="Shipment"/>: an optional summary, a model-level list, a kit input for Name while it shows, and a kit input for Code while its section is open.</summary>
    private sealed class ShipmentHost : ComponentBase
    {
        [Parameter]
        public Shipment Shipment { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public bool Summary { get; set; }

        [Parameter]
        public bool ShowCode { get; set; } = true;

        [Parameter]
        public bool ShowName { get; set; } = true;

        [Parameter]
        public bool CodeWaitsForSubmit { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Shipment>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Shipment>.Model), Shipment);
            builder.AddComponentParameter(2, nameof(FormidableForm<Shipment>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<Shipment>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder builder)
        {
            if (Summary)
            {
                builder.OpenComponent<FormidableSummary>(0);
                builder.CloseComponent();
            }

            builder.OpenComponent<FormidableModelMessage>(1);
            builder.CloseComponent();

            if (ShowName)
            {
                AddEntry(builder, 10, "Name", () => Shipment.Name, value => Shipment.Name = value, waitForSubmit: false);
            }

            if (ShowCode)
            {
                AddEntry(builder, 20, "Code", () => Shipment.Code, value => Shipment.Code = value, CodeWaitsForSubmit);
            }
        }

        private void AddEntry(RenderTreeBuilder builder, int region, string entry, Expression<Func<string?>> field, Action<string?> assign, bool waitForSubmit)
        {
            builder.OpenRegion(region);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-entry", entry);
            builder.OpenComponent<FormidableInputText>(2);
            builder.AddComponentParameter(3, "For", field);
            builder.AddComponentParameter(4, "Value", field.Compile()());
            builder.AddComponentParameter(5, "ValueChanged", EventCallback.Factory.Create(this, assign));
            builder.AddComponentParameter(8, "WaitForSubmit", waitForSubmit);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", field);
            builder.CloseComponent();
            builder.CloseElement();
            builder.CloseRegion();
        }
    }

    /// <summary>A form over <see cref="Manifest"/>: a summary, then a keyed kit input per line.</summary>
    private sealed class ManifestHost : ComponentBase
    {
        [Parameter]
        public Manifest Manifest { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Manifest>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Manifest>.Model), Manifest);
            builder.AddComponentParameter(2, nameof(FormidableForm<Manifest>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<Manifest>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableSummary>(0);
            builder.CloseComponent();
            for (var i = 0; i < Manifest.Lines.Count; i++)
            {
                var line = Manifest.Lines[i];
                builder.OpenRegion(1);
                builder.OpenElement(0, "div");
                builder.SetKey(line);
                builder.AddAttribute(1, "data-entry", $"row{i}");
                builder.OpenComponent<FormidableInputText>(2);
                builder.AddComponentParameter(3, "For", (Expression<Func<string?>>)(() => line.Sku));
                builder.AddComponentParameter(4, "Value", line.Sku);
                builder.AddComponentParameter(5, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => line.Sku = value ?? string.Empty));
                builder.CloseComponent();
                builder.CloseElement();
                builder.CloseRegion();
            }
        }
    }
}
