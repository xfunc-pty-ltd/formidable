using System.Linq.Expressions;
using AngleSharp.Dom;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// What <see cref="DelegatingModelValidator{TModel}"/> restores, at the three surfaces a wrapper
/// written against <see cref="IModelValidator{TModel}"/> alone silently costs a form.
/// <para>
/// Every test here is a PAIR over one inner validator: the same form, the same rules, the same
/// model, wrapped once by <see cref="CapabilityHidingModelValidator{TModel}"/> and once by
/// <see cref="DelegatingWrapperValidator{TModel}"/>. The bare half is what makes the delegating
/// half a statement about forwarding rather than about the inner validator — a form asserting only
/// the good outcome would pass with no wrapper in the picture at all.
/// </para>
/// </summary>
public class DelegatingModelValidatorSurfaceTests : BunitContext
{
    public DelegatingModelValidatorSurfaceTests() => Services.AddFormidable();

    // The requirement half. A field whose rules demand a value is marked and announced through the
    // delegating wrapper and is neither through the bare one, because the requirement answer has
    // no source but IRuleInspectingValidator and the bare wrapper does not present it. Mutation
    // that must break this: drop IRuleInspectingValidator from the base, and the two halves agree
    // on silence.
    [Fact]
    public void The_base_keeps_the_marker_and_the_announcement_a_bare_wrapper_loses()
    {
        var bare = RenderMarkerForm(new CapabilityHidingModelValidator<MarkerModel>(MarkerAdapter()));
        Assert.Empty(bare.FindAll("span.formidable-required"));
        Assert.Null(InputFor(bare, nameof(MarkerModel.Name)).GetAttribute("aria-required"));

        var delegating = RenderMarkerForm(new DelegatingWrapperValidator<MarkerModel>(MarkerAdapter()));
        Assert.Equal("*", MarkerFor(delegating, nameof(MarkerModel.Name))!.TextContent);
        Assert.Equal("true", InputFor(delegating, nameof(MarkerModel.Name)).GetAttribute("aria-required"));
    }

    // The draft-load half, and the point is which half of the classification moves. Both wrappers
    // disclose the wrong saved value, because deciding that needs only the model; only the
    // delegating one CONFIRMS the good one, because the list of fields a form speaks about has no
    // source but the inspection capability. Mutation that must break this: drop
    // IRuleInspectingValidator from the base, and the confirmed value goes silent among the rest.
    [Fact]
    public async Task The_base_keeps_the_confirming_half_of_a_draft_load()
    {
        var (bare, bareContext, bareDraft) =
            await LoadAsync(inner => new CapabilityHidingModelValidator<LoadedDraft>(inner));
        using (bare)
        {
            Assert.Equal(string.Empty, bareContext.FieldCssClass(Field(bareDraft, nameof(LoadedDraft.Title))));
            Assert.Equal(
                "formidable-invalid",
                bareContext.FieldCssClass(Field(bareDraft, nameof(LoadedDraft.ContactEmail))));
        }

        var (delegating, delegatingContext, delegatingDraft) =
            await LoadAsync(inner => new DelegatingWrapperValidator<LoadedDraft>(inner));
        using (delegating)
        {
            Assert.Equal(
                "formidable-valid",
                delegatingContext.FieldCssClass(Field(delegatingDraft, nameof(LoadedDraft.Title))));
            Assert.Equal(
                "formidable-invalid",
                delegatingContext.FieldCssClass(Field(delegatingDraft, nameof(LoadedDraft.ContactEmail))));
        }
    }

    // The verdict-store half, at the place it changes what a form SAYS rather than only what it
    // costs. One live pass files a verdict for every rule the submit profile selects, so a form
    // holding the rule-level capability can vouch for an edited field before any submit has run.
    // Without it there is no store to read and coverage waits for a completed submit-profile
    // evaluation, which a live pass is not — so the same field, at the same model state, wears
    // nothing. Mutation that must break this: drop IRuleLevelValidator from the base.
    [Fact]
    public async Task The_base_keeps_the_vouch_one_live_pass_earns()
    {
        Assert.Equal(string.Empty, await ClassAfterOneLivePassAsync(
            inner => new CapabilityHidingModelValidator<LoadedDraft>(inner)));
        Assert.Equal("formidable-valid", await ClassAfterOneLivePassAsync(
            inner => new DelegatingWrapperValidator<LoadedDraft>(inner)));
    }

    private static FluentValidationModelValidator<MarkerModel> MarkerAdapter() =>
        new(new MarkerValidator());

    private static FluentValidationModelValidator<LoadedDraft> DraftAdapter() =>
        new(new LoadedDraftValidator());

    private static FieldIdentifier Field(LoadedDraft draft, string name) => new(draft, name);

    /// <summary>
    /// A draft carrying one good saved value and one wrong one, loaded through an engine over the
    /// wrapper <paramref name="wrap"/> builds.
    /// </summary>
    private static async Task<(FormValidationEngine<LoadedDraft> Engine, EditContext Context, LoadedDraft Draft)>
        LoadAsync(Func<IModelValidator<LoadedDraft>, IModelValidator<LoadedDraft>> wrap)
    {
        var draft = new LoadedDraft
        {
            Title = "Quarterly plan",
            Summary = string.Empty,
            ContactEmail = "ada.lovelace",
        };
        var editContext = new EditContext(draft);
        var engine = new FormValidationEngine<LoadedDraft>(
            draft, editContext, wrap(DraftAdapter()), new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        await engine.DiscloseLoadedValuesAsync();
        return (engine, editContext, draft);
    }

    /// <summary>
    /// The class a title edited to a passing value wears once the live pass that edit starts has
    /// settled, with nothing submitted and no validity tracking asked for.
    /// </summary>
    private static async Task<string> ClassAfterOneLivePassAsync(
        Func<IModelValidator<LoadedDraft>, IModelValidator<LoadedDraft>> wrap)
    {
        var draft = new LoadedDraft();
        var editContext = new EditContext(draft);
        using var engine = new FormValidationEngine<LoadedDraft>(
            draft, editContext, wrap(DraftAdapter()), new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        var title = Field(draft, nameof(LoadedDraft.Title));
        using var registration = engine.Registry.Register(title);

        draft.Title = "Quarterly plan";
        editContext.NotifyFieldChanged(title);
        await WaitForQuietAsync(engine);

        var native = editContext.FieldCssClass(title);
        Assert.Equal(native, FormidableCss.Compute(engine.GetFieldState(title), engine.Options.CssClasses));
        return native;
    }

    private static async Task WaitForQuietAsync(IFormValidationEngine engine)
    {
        for (var attempt = 0; attempt < 100 && engine.IsValidating; attempt++)
        {
            await Task.Delay(5);
        }

        await Task.Yield();
    }

    private static IElement? MarkerFor(IRenderedComponent<FormidableForm<MarkerModel>> cut, string field) =>
        cut.FindAll($"[data-field='{field}'] span.formidable-required").SingleOrDefault();

    private static IElement InputFor(IRenderedComponent<FormidableForm<MarkerModel>> cut, string field) =>
        cut.FindAll($"[data-field='{field}'] input").Single();

    /// <summary>One labelled field: the marker inside the label, then the input.</summary>
    private IRenderedComponent<FormidableForm<MarkerModel>> RenderMarkerForm(IModelValidator<MarkerModel> validator)
    {
        var model = new MarkerModel();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<MarkerModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", validator);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenElement(0, "label");
                inner.AddAttribute(1, "data-field", nameof(MarkerModel.Name));
                inner.AddContent(2, nameof(MarkerModel.Name));

                inner.OpenComponent<FormidableRequiredIndicator<string>>(3);
                inner.AddComponentParameter(4, "For", (Expression<Func<string>>)(() => model.Name));
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(5);
                inner.AddComponentParameter(6, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.CloseComponent();

                inner.CloseElement();
            }));
            builder.CloseComponent();
        });

        return cut.FindComponent<FormidableForm<MarkerModel>>();
    }
}
