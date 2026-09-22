using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

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

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Label);
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

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderSummary(bool withWitness)
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            // Nothing here renders a field, so without the override every client submit error
            // would be filtered out as belonging to something unrendered.
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
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
                            item.AddComponentParameter(1, nameof(EntryWitness.Label), entry.Issue.Message);
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
}
