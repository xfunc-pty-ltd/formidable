using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A summary entry that outlives a pass has to be the SAME entry afterwards. Blazor matches
/// unkeyed siblings by sequence number, so correcting the field the first entry names would
/// rewrite the text of every entry below it and drop the last one — a whole band's worth of churn
/// where one node should have left. The entries carry a key for that reason, and these are the two
/// properties the key owes: it tracks an entry across renders, and it stays unique among siblings
/// even when two entries are equal.
/// </summary>
/// <remarks>
/// A rendered element's identity is not observable from bUnit — its DOM is re-parsed from markup
/// rather than patched, so two renders of the same markup never share a node whatever the diff
/// did. What IS observable is a child COMPONENT's identity, and a keyed subtree carries its
/// components with it: <see cref="EntryWitness"/> renders inside <c>ItemTemplate</c> and its
/// instance is the entry's identity made visible.
/// </remarks>
public class FormidableSummaryEntryIdentityTests : BunitContext
{
    /// <summary>
    /// A component with nothing to it but the fact that it is itself: rendered inside a summary
    /// entry, the instance the renderer keeps for it says which entry the framework thinks it is
    /// looking at.
    /// </summary>
    private sealed class EntryWitness : ComponentBase
    {
        [Parameter] public string Label { get; set; } = string.Empty;

        // The issue's state as the entry last handed it over, which says which pass the
        // instance was last rendered for.
        [Parameter] public object? State { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Label);
    }

    /// <summary>A <c>WithState</c> payload of a plain class, which compares by reference.</summary>
    private sealed class StatePayload
    {
    }

    /// <summary>
    /// Description's rule attaches a fresh <see cref="StatePayload"/> each time it runs, beside a
    /// Customer rule that gives the page another field to edit.
    /// </summary>
    private sealed class StatefulDescriptionValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required").WithState(_ => new StatePayload());
            RuleFor(x => x.Customer).NotNull().WithMessage("A customer is required");
        }
    }

    /// <summary>
    /// Two Description rules alike in every member but their <see cref="StatePayload"/>, so their
    /// issues are unequal records that still say the same thing.
    /// </summary>
    private sealed class TwinStateValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required").WithState(_ => new StatePayload());
            RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required").WithState(_ => new StatePayload());
        }
    }

    public FormidableSummaryEntryIdentityTests()
    {
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    /// <summary>
    /// Renders a form holding a summary and nothing else. With <paramref name="withWitness"/>,
    /// each entry renders an <see cref="EntryWitness"/> labelled by <paramref name="label"/>, or
    /// by the issue's message when no label is given.
    /// </summary>
    private IRenderedComponent<FormidableForm<EngineOrder>> RenderSummary(
        bool withWitness, FormidableOptions? options = null, Func<VisibleIssue, string>? label = null)
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            // Nothing here renders a field, so without the override every client submit error
            // would be filtered out as belonging to something unrendered. Options a test passes
            // replace it.
            builder.AddComponentParameter(2, "Options", options ?? new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit), false);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                if (withWitness)
                {
                    inner.AddComponentParameter(1, nameof(FormidableSummary.ItemTemplate),
                        (RenderFragment<VisibleIssue>)(entry => item =>
                        {
                            item.OpenComponent<EntryWitness>(0);
                            item.AddComponentParameter(1, nameof(EntryWitness.Label), label is null ? entry.Issue.Message : label(entry));
                            item.AddComponentParameter(2, nameof(EntryWitness.State), entry.Issue.State);
                            item.CloseComponent();
                        }));
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    private static void Submit(IRenderedComponent<FormidableForm<EngineOrder>> form, int expectedEntries)
    {
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() =>
            Assert.Equal(expectedEntries, form.FindAll("li.formidable-summary__item").Count));
    }

    /// <summary>
    /// The entry whose problem is fixed is the entry that goes, and the ones below it are the same
    /// entries afterwards — which is what a key buys and sequence-number matching cannot. Mutation
    /// that must break it: drop the <c>SetKey</c> call from the entry loop, and each surviving
    /// witness is the instance that rendered the entry one place above it.
    /// </summary>
    [Fact]
    public async Task Correcting_one_entry_leaves_the_others_the_same_entries()
    {
        var validator = new ToggleableTripleValidator();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(validator);

        var form = RenderSummary(withWitness: true);
        Submit(form, expectedEntries: 3);

        var before = form.FindComponents<EntryWitness>().Select(c => c.Instance).ToList();
        Assert.Equal(
            ["First problem", "Second problem", "Third problem"],
            before.Select(w => w.Label));

        validator.FirstFails = false;
        Submit(form, expectedEntries: 2);

        var after = form.FindComponents<EntryWitness>().Select(c => c.Instance).ToList();
        Assert.Equal(["Second problem", "Third problem"], after.Select(w => w.Label));

        // Reference identity, not equality: these have to be the very instances that rendered the
        // same two entries before the correction.
        Assert.Same(before[1], after[0]);
        Assert.Same(before[2], after[1]);
        Assert.DoesNotContain(before[0], after);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// Two issues a validator declares identically are equal records, so keying an entry by its
    /// value alone would give two siblings the same key — which Blazor rejects outright, and not
    /// at the render that paints them: the first render succeeds and the throw lands on the next
    /// DIFF over that band, so a form would look right and fall over on the pass after. Mutation
    /// that must break it: key the entry by <c>entry</c> instead of pairing it with its ordinal
    /// among equal entries, and the second submit throws "More than one sibling of element 'li'
    /// has the same key value".
    /// </summary>
    [Fact]
    public async Task Two_identical_issues_render_as_two_entries_and_survive_the_next_pass()
    {
        var validator = new DuplicateIssueValidator();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(validator);

        var form = RenderSummary(withWitness: false);
        Submit(form, expectedEntries: 3);

        var visible = form.Instance.Engine!.GetVisibleIssues();
        var duplicates = visible.Where(v => v.Issue.Message == "Description is required").ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.Single(duplicates.Distinct());

        // The band has to CHANGE for its entries to be matched against each other, which is where
        // a repeated key is caught: dropping the third entry is the cheapest way to make it.
        validator.ThirdFails = false;
        Submit(form, expectedEntries: 2);

        Assert.Equal(
            ["Description is required", "Description is required"],
            form.FindAll("li.formidable-summary__item").Select(li => li.TextContent));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A model-level entry takes its name from <c>ModelLevelDisplayName</c>, which a page can
    /// re-voice while the entry shows, as a runtime language switch does. The entry still speaks
    /// for the same issue on the same field, so it has to stay the same entry: a replaced one
    /// would take focus off its button and be announced again. Mutation that must break it: add
    /// <c>entry.DisplayName</c> to the entry's key, and the witness after the re-voicing is a new
    /// instance.
    /// </summary>
    [Fact]
    public async Task Re_voicing_the_model_level_name_keeps_the_entry()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(new ToggleableTripleValidator());
        var options = new FormidableOptions();
        var form = RenderSummary(withWitness: true, options, label: entry => entry.DisplayName ?? string.Empty);

        // Nothing renders the fields the validator fails, so the blocked submit can show only the
        // gate's explanation, which names no field of its own.
        Submit(form, expectedEntries: 1);
        var before = Assert.Single(form.FindComponents<EntryWitness>()).Instance;
        Assert.Equal("This form", before.Label);

        options.ModelLevelDisplayName = "Ce formulaire";
        form.Render();

        var after = Assert.Single(form.FindComponents<EntryWitness>()).Instance;
        Assert.Equal("Ce formulaire", after.Label);
        Assert.Same(before, after);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A rule's <c>WithState</c> payload is a new instance each time the rule runs, and a plain
    /// class compares by reference, so the issue it rides on is a new record after every re-check.
    /// The entry still speaks for the same issue on the same field, so it has to stay the same
    /// entry: a replaced one would take focus off its button and be announced again. Mutation
    /// that must break it: key the entry by <c>(entry.Field, entry.Issue, occurrence)</c>, which
    /// puts the state in the key, and the witness after the edit is a new instance.
    /// </summary>
    [Fact]
    public async Task A_WithState_payload_of_a_plain_class_keeps_its_entry_across_passes()
    {
        var clock = new FakeTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(new StatefulDescriptionValidator());

        var form = RenderSummary(withWitness: true);
        Submit(form, expectedEntries: 2);

        var before = DescriptionWitness(form);
        var stateBefore = before.State;
        Assert.IsType<StatePayload>(stateBefore);

        // An edit to another field after a blocked submit re-checks the whole form once the
        // debounce has passed, which runs Description's rule again and hands it a new payload.
        var engine = form.Instance.Engine!;
        var customer = new FieldIdentifier(form.Instance.Model!, nameof(EngineOrder.Customer));
        await form.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(customer));
        clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1));

        // Waiting on the payload the entry renders with, not on the engine, is what makes the
        // identity check below read the render that followed the re-check.
        form.WaitForAssertion(() => Assert.NotSame(stateBefore, DescriptionWitness(form).State));

        Assert.Same(before, DescriptionWitness(form));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A pin of the agreement between the key and its count. Two issues alike in everything but a
    /// class-payload state are unequal records that key alike, so their occurrence alone tells
    /// them apart, and it has to be counted on the key the entry carries. Mutation that must break
    /// it: count occurrences by <c>(entry.Field, entry.Issue)</c> while keying by
    /// <c>IssueKey</c>, and both entries take occurrence 0 under one key, so the second submit
    /// throws "More than one sibling of element 'li' has the same key value".
    /// </summary>
    [Fact]
    public async Task Two_issues_differing_only_in_State_render_as_two_entries_and_survive_the_next_render()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(new TwinStateValidator());

        var form = RenderSummary(withWitness: false);
        Submit(form, expectedEntries: 2);

        var twins = form.Instance.Engine!.GetVisibleIssues();
        Assert.Equal(2, twins.Count);
        Assert.NotEqual(twins[0].Issue, twins[1].Issue);
        Assert.Equal(twins[0].Issue with { State = null }, twins[1].Issue with { State = null });

        Submit(form, expectedEntries: 2);

        Assert.Equal(
            ["Description is required", "Description is required"],
            form.FindAll("li.formidable-summary__item").Select(li => li.TextContent));

        await Services.DisposeAsync();
    }

    private static EntryWitness DescriptionWitness(IRenderedComponent<FormidableForm<EngineOrder>> form) =>
        Assert.Single(form.FindComponents<EntryWitness>(), c => c.Instance.Label == "Description is required").Instance;
}
