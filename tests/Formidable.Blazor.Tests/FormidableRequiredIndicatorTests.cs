using System.Linq.Expressions;
using AngleSharp.Dom;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The required-field marker, end to end: what the rules are read to demand, what the marker
/// draws for each of the three answers, what assistive technology is told, and what a form says
/// when the rules cannot be read.
/// <para>
/// The standing property, and the one to preserve when adding to this file: nothing here
/// fabricates a requirement answer. Every validator below is a real one, so an answer comes
/// either from FluentValidation's own descriptor or from a validator that genuinely has none to
/// read — never from a double written to agree with the code under test.
/// </para>
/// <para>
/// Most tests render a real form over <see cref="MarkerValidator"/>. Several depart from it: a
/// different validator, a different model, or no render at all. Each departure is chosen for the
/// property that test pins, and that property is stated on the test itself. A test added here
/// that needs its own departure states it the same way, which is why this paragraph describes
/// the convention rather than listing who currently breaks it.
/// </para>
/// </summary>
public class FormidableRequiredIndicatorTests : BunitContext
{
    public FormidableRequiredIndicatorTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<MarkerModel>, MarkerValidator>();
    }

    // The unconditional NotEmpty() on Name is read as a demand and drawn; Nickname carries no
    // rule at all and is drawn nothing. Mutation that must break this: marking every field.
    [Fact]
    public void A_field_the_rules_demand_a_value_for_is_marked_and_one_they_say_nothing_about_is_not()
    {
        var cut = RenderForm(new MarkerModel());

        Assert.Equal("*", MarkerFor(cut, nameof(MarkerModel.Name))!.TextContent);
        Assert.Null(MarkerFor(cut, nameof(MarkerModel.Nickname)));
    }

    // The glyph is decoration and says so; the fact rides on the input, where a screen reader
    // meets it as a property of the control rather than as a character in the label's text.
    // Mutation that must break this: dropping aria-hidden from the marker, or aria-required from
    // the input.
    [Fact]
    public void The_marker_is_hidden_from_assistive_technology_and_the_input_announces_the_demand()
    {
        var cut = RenderForm(new MarkerModel());

        Assert.Equal("true", MarkerFor(cut, nameof(MarkerModel.Name))!.GetAttribute("aria-hidden"));
        Assert.Equal("true", InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("aria-required"));
        Assert.Null(InputFor(cut, nameof(MarkerModel.Nickname)).GetAttribute("aria-required"));
    }

    // Nominee's presence rule is reached only through a When(), so whether it applies cannot be
    // decided without evaluating the condition against the model — which inspection does not do.
    // The marker draws nothing and the input announces nothing, and the two agreeing is the
    // point: a mark a screen reader is not told about, or an announcement with no mark, would be
    // one field described two ways. The third assert is what makes the silence a rendering
    // decision rather than a detection failure — the tri-state reaches a consumer intact, so a
    // page that wants to say something there can.
    [Fact]
    public void A_conditionally_required_field_is_neither_marked_nor_announced_but_is_still_reported()
    {
        var model = new MarkerModel();
        var cut = RenderForm(model);

        Assert.Null(MarkerFor(cut, nameof(MarkerModel.Nominee)));
        Assert.Null(InputFor(cut, nameof(MarkerModel.Nominee)).GetAttribute("aria-required"));
        Assert.Equal(
            FieldRequirement.ConditionallyRequired,
            EngineOf(cut).GetFieldRequirement(new FieldIdentifier(model, nameof(MarkerModel.Nominee))));
    }

    // Handle's presence is written as a predicate, which is indistinguishable from any other
    // predicate, so the rules report nothing and the field is unmarked until the form says
    // otherwise. Mutation that must break this: ignoring RequiredOverride, which leaves the
    // second half asserting what the first half already established.
    [Fact]
    public void The_override_marks_a_field_whose_presence_rule_cannot_be_read()
    {
        var unmarked = RenderForm(new MarkerModel());
        Assert.Null(MarkerFor(unmarked, nameof(MarkerModel.Handle)));
        Assert.Null(InputFor(unmarked, nameof(MarkerModel.Handle)).GetAttribute("aria-required"));

        var options = new FormidableOptions
        {
            RequiredOverride = field => field.FieldName == nameof(MarkerModel.Handle)
                ? FieldRequirement.Required
                : null,
        };
        var cut = RenderForm(new MarkerModel(), options);

        Assert.Equal("*", MarkerFor(cut, nameof(MarkerModel.Handle))!.TextContent);
        Assert.Equal("true", InputFor(cut, nameof(MarkerModel.Handle)).GetAttribute("aria-required"));
        // The other half of "return null to defer": the rules' own verdict still stands
        // everywhere the delegate declines to answer.
        Assert.Equal("*", MarkerFor(cut, nameof(MarkerModel.Name))!.TextContent);
    }

    // And the other direction, which is the half a one-way override would miss: a form whose
    // page fills a demanded value in itself says so, and the field stops being marked and stops
    // being announced. Mutation that must break this: ignoring RequiredOverride.
    [Fact]
    public void The_override_unmarks_a_field_the_rules_do_demand()
    {
        var options = new FormidableOptions
        {
            RequiredOverride = field => field.FieldName == nameof(MarkerModel.Name)
                ? FieldRequirement.NotRequired
                : null,
        };
        var cut = RenderForm(new MarkerModel(), options);

        Assert.Null(MarkerFor(cut, nameof(MarkerModel.Name)));
        Assert.Null(InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("aria-required"));
    }

    // Suppression is form-wide and total for the drawn marker — no element at all, not an empty
    // one — and deliberately not total for the announcement, which is a fact about the input
    // rather than a decoration. Mutation that must break this: ignoring ShowRequiredIndicators,
    // or letting it gate aria-required too.
    [Fact]
    public void Suppressing_the_indicator_removes_every_marker_and_keeps_the_announcement()
    {
        var cut = RenderForm(new MarkerModel(), new FormidableOptions { ShowRequiredIndicators = false });

        Assert.Empty(cut.FindAll("span.formidable-required"));
        Assert.Equal("true", InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("aria-required"));
    }

    // Empty content is not the off switch: the marker element still renders, carrying the class
    // and aria-hidden and nothing inside — the shape a stylesheet's ::before needs on the page
    // before it can draw a CSS-only glyph. Mutation that must break this: treating "" as off,
    // which removes the very element the CSS-drawn route exists to provide.
    [Fact]
    public void Empty_content_renders_the_empty_marker_element_for_a_css_drawn_glyph()
    {
        var cut = RenderForm(new MarkerModel(), new FormidableOptions { RequiredIndicatorContent = "" });

        var marker = MarkerFor(cut, nameof(MarkerModel.Name))!;
        Assert.Equal(string.Empty, marker.TextContent);
        Assert.Equal("formidable-required", marker.GetAttribute("class"));
        Assert.Equal("true", marker.GetAttribute("aria-hidden"));
    }

    // The configured content is what the marker holds, and nothing else about it changes — the
    // class and aria-hidden are the seam a consumer's stylesheet keys on.
    [Fact]
    public void The_marker_renders_the_configured_content()
    {
        var cut = RenderForm(new MarkerModel(), new FormidableOptions { RequiredIndicatorContent = "(required)" });

        var marker = MarkerFor(cut, nameof(MarkerModel.Name))!;
        Assert.Equal("(required)", marker.TextContent);
        Assert.Equal("formidable-required", marker.GetAttribute("class"));
        Assert.Equal("true", marker.GetAttribute("aria-hidden"));
    }

    // Reference's NotEmpty() sits in the submit bucket while its length rule sits in the draft
    // one, so the same field answers differently under the two profiles. The submit profile is
    // what decides, because "required" on a form means "required before this can be submitted".
    // Mutation that must break this: reading the rules without filtering by the profile — the
    // narrowed form then marks Reference as well.
    [Fact]
    public void The_submit_profile_decides_which_fields_are_marked()
    {
        var wide = RenderForm(new MarkerModel());
        Assert.Equal("*", MarkerFor(wide, nameof(MarkerModel.Reference))!.TextContent);

        var narrowed = RenderForm(
            new MarkerModel(),
            new FormidableOptions { SubmitProfile = ValidationProfile.Draft });

        Assert.Null(MarkerFor(narrowed, nameof(MarkerModel.Reference)));
        Assert.Null(InputFor(narrowed, nameof(MarkerModel.Reference)).GetAttribute("aria-required"));
        // Name's NotEmpty() is in the draft bucket, so narrowing does not silence it — which is
        // what makes the two asserts above a profile filter rather than detection switched off.
        Assert.Equal("*", MarkerFor(narrowed, nameof(MarkerModel.Name))!.TextContent);
    }

    // A presence rule merged in with Include() is one the form really enforces, so the field it
    // demands is marked. Include()d rules carry no property name of their own at the including
    // validator's level, so a reading that only enumerates what a validator declares for itself
    // drops them - and the field then goes unmarked and unannounced while a submit blocks on it.
    // Nickname is the control field the rest of this file relies on being unruled, so the mark
    // here can only have come from the included part. Mutation this breaks: stop descending into
    // an Include()d validator.
    [Fact]
    public void A_presence_rule_reached_through_include_is_marked()
    {
        var cut = RenderForm(
            new MarkerModel(),
            validator: new FluentValidationModelValidator<MarkerModel>(new IncludingMarkerValidator()));

        Assert.Equal("*", MarkerFor(cut, nameof(MarkerModel.Nickname))!.TextContent);
        Assert.Equal("true", InputFor(cut, nameof(MarkerModel.Nickname)).GetAttribute("aria-required"));
    }

    // A presence rule declared inside a CHILD validator demands its field just as plainly as one
    // declared on the root, and it is answered under the child's own path. The two roots
    // compared here declare the same demand two ways - one reaching through the property path,
    // one delegating to a validator for the child type - and asserting them equal is what says
    // the answer is about the rule rather than about how it was written. Mutation this breaks:
    // stop descending into child validators, and the delegated form alone goes back to
    // reporting NotRequired, which is what makes the equality discriminate.
    [Fact]
    public void A_presence_rule_inside_a_child_validator_is_answered_under_the_childs_path()
    {
        var throughThePath = RequirementOf(new NestedRootValidator());
        var throughAChildValidator = RequirementOf(new DelegatingNestedRootValidator());

        Assert.Equal(FieldRequirement.Required, throughThePath);
        Assert.Equal(throughThePath, throughAChildValidator);
    }

    // The same demand at the surface it actually changes: what the reader sees is a marker and
    // what a screen reader is told is aria-required, and a requirement nothing draws is not the
    // behaviour that moved. The two assertions are the pair the Include sibling above makes, for
    // the same reason - a mark nobody is told about, or an announcement with no mark, would be
    // one field described two ways.
    [Fact]
    public void A_presence_rule_inside_a_child_validator_marks_the_rendered_field()
    {
        var model = new NestedRoot();
        var cut = RenderNestedForm(model, new FluentValidationModelValidator<NestedRoot>(new DelegatingNestedRootValidator()));

        Assert.Equal("*", cut.FindAll("label[data-field='City'] span.formidable-required").Single().TextContent);
        Assert.Equal("true", cut.FindAll("label[data-field='City'] input").Single().GetAttribute("aria-required"));
    }

    // A presence rule declared per collection row is a template - Rows[].City - and the fields
    // it demands are the rows the model actually holds. The marker and the announcement land on
    // a row-bound field exactly as on a top-level one, and the SECOND row is the one asserted,
    // so an answer that reached only a first entry could not pass. The two assertions are the
    // pair the Include sibling makes, for the same reason - a mark nobody is told about, or an
    // announcement with no mark, would be one field described two ways. Mutation that must
    // break this: building the requirement map from the scalar declared paths alone, skipping
    // templates instead of expanding them against the model's rows.
    [Fact]
    public void A_presence_rule_declared_per_row_marks_the_rendered_row_field()
    {
        var model = new RowsRoot();
        var cut = RenderRowsForm(model, new FluentValidationModelValidator<RowsRoot>(new RowsRootValidator()));

        Assert.Equal("*", cut.FindAll("label[data-field='Row1'] span.formidable-required").Single().TextContent);
        Assert.Equal("true", cut.FindAll("label[data-field='Row1'] input").Single().GetAttribute("aria-required"));
    }

    // A validator with no inspection capability answers "not known to be required" for every
    // field, and the marker draws nothing rather than guessing. The override is the whole of
    // what such a form has, which is why it is not optional.
    [Fact]
    public void A_validator_that_cannot_be_inspected_marks_nothing()
    {
        var cut = RenderForm(new MarkerModel(), validator: new OpaqueValidator());

        Assert.Empty(cut.FindAll("span.formidable-required"));
        Assert.Null(InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("aria-required"));
    }

    // The rules are walked for the form, not for each field on each render: this form renders
    // five bound fields, every one of which asks on every render it takes part in. A per-ask
    // implementation reads the validator five times for the first render alone. Mutation that
    // must break this: reading the validator on every ask.
    [Fact]
    public void The_rules_are_read_for_the_form_rather_than_per_field_per_render()
    {
        var model = new MarkerModel();
        var field = new FieldIdentifier(model, nameof(MarkerModel.Name));
        var counting = new CountingInspector(new FluentValidationModelValidator<MarkerModel>(new MarkerValidator()));
        var cut = RenderForm(model, validator: counting);

        Assert.Equal("*", MarkerFor(cut, nameof(MarkerModel.Name))!.TextContent);
        Assert.Equal(1, counting.RuleMapReads);

        // The five registrations of that first render are a move in the rendered field set, and
        // the answer is dropped there because its keys are resolved against the model graph. So
        // the render this notification causes rebuilds once — and that is the whole cost of it,
        // however many components ask and however many renders they ask across.
        cut.InvokeAsync(() => EngineOf(cut).EditContext.NotifyFieldChanged(field));
        cut.WaitForAssertion(() =>
            Assert.Contains("formidable-invalid", InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("class")));
        Assert.Equal(2, counting.RuleMapReads);

        // A second pass over a settled field set asks five more times and reads nothing.
        model.Name = "filled in";
        cut.InvokeAsync(() => EngineOf(cut).EditContext.NotifyFieldChanged(field));
        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("formidable-invalid", InputFor(cut, nameof(MarkerModel.Name)).GetAttribute("class")));
        Assert.Equal(2, counting.RuleMapReads);
    }

    // The one limit the frozen XML on GetFieldRequirement carries that is not about what the
    // rules say, pinned in the direction the prose got wrong once already. An answer is keyed by
    // the declared path resolved against the model graph — exactly the way an ISSUE is keyed —
    // while a component asks with the identifier it resolved when it last bound to the cascaded
    // context. Replacing a nested object in place does NOT separate those two: both go on naming
    // the object that has been replaced, and the field keeps its answer. What separates them is
    // the next DERIVATION, which files the member under the new owner while an unmoved component
    // still asks under the old one — so the rebuild is what strands the asker, not the swap. The
    // pair of asks after that rebuild is the whole cause in two lines: at one instant a freshly
    // resolved identifier is answered and the held one is not, which says the map is current and
    // the asker is stale rather than the other way round. Nothing here renders and nothing here
    // waits, so the ordering is the test's own.
    //
    // Mutation that must break this: dropping the cached answer on every ask (the guard in
    // Requirements() never hitting). The map then re-derives the moment the swap happens, so
    // `held` misses straight away and the post-swap assert — the one that reads Required with
    // nothing yet rebuilt — reports NotRequired instead. That is the inverted world an earlier
    // draft of that XML described, which is what makes it the assert worth having.
    [Fact]
    public void A_nested_owner_swap_strands_the_asker_at_the_next_rebuild_rather_than_at_the_swap()
    {
        var model = new NestedRoot();
        var replaced = model.Child;
        using var engine = new FormValidationEngine<NestedRoot>(
            model,
            new EditContext(model),
            new FluentValidationModelValidator<NestedRoot>(new NestedRootValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        var held = FieldIdentifier.Create(() => model.Child.City);
        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(held));

        // The swap alone. Both the answer and the held identifier still name the replaced child,
        // so they still agree and the field is still answered.
        model.Child = new NestedChild();
        Assert.NotSame(replaced, model.Child);
        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(held));

        // The next derivation. A move in the rendered field set drops the answer, and rebuilding
        // it files the member under the child the graph now holds.
        engine.OnRenderedFieldsChanged();
        Assert.Equal(FieldRequirement.NotRequired, engine.GetFieldRequirement(held));
        Assert.Equal(
            FieldRequirement.Required,
            engine.GetFieldRequirement(FieldIdentifier.Create(() => model.Child.City)));

        // And no later move repairs it: every derivation files against the graph as it then
        // stands, so only the component rebinding — its host rebuilding the engine and registry —
        // ever gives the asker a current identifier again.
        engine.OnRenderedFieldsChanged();
        Assert.Equal(FieldRequirement.NotRequired, engine.GetFieldRequirement(held));
    }

    // A marker is not an input, so it registers nothing — and the consequence is bigger than the
    // registry entry: registration is the submit channel's visibility gate, so a marker that
    // registered would make a field with no control on the page eligible to have its submit error
    // disclosed. Here Name is failing NotEmpty() and the only thing speaking for it is the marker,
    // so a blocked submit must suppress that error rather than show it, and the form must fall
    // back on the all-suppressed gate instead. Mutation that must break this: returning
    // context.Registry.Register(...) from the marker's Register.
    [Fact]
    public void The_marker_registers_nothing_so_a_field_it_alone_speaks_for_stays_undisclosed()
    {
        var model = new MarkerModel();
        var field = new FieldIdentifier(model, nameof(MarkerModel.Name));
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<MarkerModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableRequiredIndicator<string>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string>>)(() => model.Name));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<MarkerModel>>();

        // The marker rendered — so the field is one the engine has an answer for, and the
        // registry's silence below is the marker's own doing rather than the marker being absent.
        Assert.Equal("*", form.Find("span.formidable-required").TextContent);
        Assert.False(form.Instance.Engine!.Registry.IsRegistered(field));

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.True(form.Instance.Engine!.HasSubmitted));
        Assert.Empty(form.Instance.Engine!.GetIssues(field));
    }

    private static IFormValidationEngine EngineOf(IRenderedComponent<FormidableForm<MarkerModel>> cut) =>
        cut.Instance.Engine!;

    private static IElement? MarkerFor(IRenderedComponent<FormidableForm<MarkerModel>> cut, string field) =>
        cut.FindAll($"[data-field='{field}'] span.formidable-required").SingleOrDefault();

    private static IElement InputFor(IRenderedComponent<FormidableForm<MarkerModel>> cut, string field) =>
        cut.FindAll($"[data-field='{field}'] input").Single();

    /// <summary>
    /// The submit-profile requirement an engine over <paramref name="validator"/> reports for
    /// <c>Child.City</c> — the one field both nested-root validators demand, reached the same
    /// way from each, so the two answers are comparable.
    /// </summary>
    private static FieldRequirement RequirementOf(IValidator<NestedRoot> validator)
    {
        var model = new NestedRoot();
        using var engine = new FormValidationEngine<NestedRoot>(
            model,
            new EditContext(model),
            new FluentValidationModelValidator<NestedRoot>(validator),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        return engine.GetFieldRequirement(FieldIdentifier.Create(() => model.Child.City));
    }

    /// <summary>
    /// One labelled field bound to a nested member, in the shape a page writes — the surface a
    /// requirement answered under a child's own path has to reach.
    /// </summary>
    private IRenderedComponent<FormidableForm<NestedRoot>> RenderNestedForm(
        NestedRoot model, IModelValidator<NestedRoot> validator)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<NestedRoot>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", validator);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                var sequence = 0;
                Field(
                    inner,
                    ref sequence,
                    nameof(NestedChild.City),
                    () => model.Child.City,
                    () => model.Child.City);
            }));
            builder.CloseComponent();
        });

        return cut.FindComponent<FormidableForm<NestedRoot>>();
    }

    /// <summary>
    /// One labelled field per collection row, in the shape a page's row loop writes — each
    /// accessor closing over its own row instance, which is the identifier the components hold
    /// and the one the engine's expanded template entries must land on.
    /// </summary>
    private IRenderedComponent<FormidableForm<RowsRoot>> RenderRowsForm(
        RowsRoot model, IModelValidator<RowsRoot> validator)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<RowsRoot>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", validator);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                var sequence = 0;
                for (var index = 0; index < model.Rows.Count; index++)
                {
                    var row = model.Rows[index];
                    Field(inner, ref sequence, $"Row{index}", () => row.City, () => row.City);
                }
            }));
            builder.CloseComponent();
        });

        return cut.FindComponent<FormidableForm<RowsRoot>>();
    }

    private IRenderedComponent<FormidableForm<MarkerModel>> RenderForm(
        MarkerModel model,
        FormidableOptions? options = null,
        IModelValidator<MarkerModel>? validator = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<MarkerModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Options", options);
            builder.AddComponentParameter(3, "Validator", validator);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                var sequence = 0;
                Field(inner, ref sequence, nameof(MarkerModel.Name), () => model.Name, () => model.Name);
                Field(inner, ref sequence, nameof(MarkerModel.Nickname), () => model.Nickname, () => model.Nickname);
                Field(inner, ref sequence, nameof(MarkerModel.Handle), () => model.Handle, () => model.Handle);
                Field(inner, ref sequence, nameof(MarkerModel.Nominee), () => model.Nominee, () => model.Nominee);
                Field(inner, ref sequence, nameof(MarkerModel.Reference), () => model.Reference, () => model.Reference);
            }));
            builder.CloseComponent();
        });

        return cut.FindComponent<FormidableForm<MarkerModel>>();
    }

    /// <summary>
    /// One labelled field, in the shape a page writes: the marker inside the label after its
    /// text, then the input. The wrapper carries the field's name so a test can address either
    /// half. The two accessors name the same property and differ only in nullability, which is
    /// what the marker's and the input's own type parameters ask for.
    /// </summary>
    private static void Field(
        RenderTreeBuilder builder,
        ref int sequence,
        string name,
        Expression<Func<string>> marked,
        Expression<Func<string?>> bound)
    {
        builder.OpenElement(sequence++, "label");
        builder.AddAttribute(sequence++, "data-field", name);
        builder.AddContent(sequence++, name);

        builder.OpenComponent<FormidableRequiredIndicator<string>>(sequence++);
        builder.AddComponentParameter(sequence++, "For", marked);
        builder.CloseComponent();

        builder.OpenComponent<FormidableInputText>(sequence++);
        builder.AddComponentParameter(sequence++, "For", bound);
        builder.CloseComponent();

        builder.CloseElement();
    }
}

public sealed class MarkerModel
{
    /// <summary>Presence demanded unconditionally, in the draft bucket — required under both profiles.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>No rule of any kind.</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Presence written as a predicate, which reading rules cannot see.</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>Presence demanded only when a condition holds.</summary>
    public string Nominee { get; set; } = string.Empty;

    /// <summary>A format rule in the draft bucket and presence in the submit bucket.</summary>
    public string Reference { get; set; } = string.Empty;
}

/// <summary>
/// A root whose demanded field lives on a nested object, so its answer is filed against that
/// object rather than against the root — the only shape the owner-swap limit can be seen in.
/// </summary>
public sealed class NestedRoot
{
    public NestedChild Child { get; set; } = new();
}

public sealed class NestedChild
{
    public string City { get; set; } = string.Empty;
}

/// <summary>
/// A root whose demanded fields live one per collection row — the shape the template
/// <c>Rows[].City</c> answers for. Two rows, so an answer reaching every row is told apart
/// from one reaching a first entry alone.
/// </summary>
public sealed class RowsRoot
{
    public List<NestedChild> Rows { get; set; } = [new(), new()];
}

/// <summary>A presence demand declared once, about every row.</summary>
public sealed class RowsRootValidator : AbstractValidator<RowsRoot>
{
    public RowsRootValidator() => RuleForEach(m => m.Rows).SetValidator(new NestedChildValidator());
}

public sealed class NestedRootValidator : AbstractValidator<NestedRoot>
{
    public NestedRootValidator()
    {
        RuleFor(m => m.Child.City).NotEmpty();
    }
}

/// <summary>The same demand as <see cref="NestedRootValidator"/>, delegated to a child validator
/// for the child type rather than reached through the property path.</summary>
public sealed class NestedChildValidator : AbstractValidator<NestedChild>
{
    public NestedChildValidator() => RuleFor(c => c.City).NotEmpty();
}

/// <summary>A root that hands its child to <see cref="NestedChildValidator"/>.</summary>
public sealed class DelegatingNestedRootValidator : AbstractValidator<NestedRoot>
{
    public DelegatingNestedRootValidator() => RuleFor(m => m.Child).SetValidator(new NestedChildValidator());
}

public sealed class MarkerValidator : DraftSubmitValidator<MarkerModel>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(m => m.Name).NotEmpty();
        RuleFor(m => m.Reference).MaximumLength(5);
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(m => m.Reference).NotEmpty();
        RuleFor(m => m.Handle).Must(h => !string.IsNullOrWhiteSpace(h));
        RuleFor(m => m.Nominee).NotEmpty().When(m => m.Name.Length > 0);
    }
}

/// <summary>A part validator whose presence rule reaches a form only through Include().</summary>
public sealed class IncludedMarkerPartValidator : AbstractValidator<MarkerModel>
{
    public IncludedMarkerPartValidator() => RuleFor(m => m.Nickname).NotEmpty();
}

/// <summary>One rule of its own and one merged in — the shape Include() produces.</summary>
public sealed class IncludingMarkerValidator : AbstractValidator<MarkerModel>
{
    public IncludingMarkerValidator()
    {
        RuleFor(m => m.Name).NotEmpty();
        Include(new IncludedMarkerPartValidator());
    }
}

/// <summary>
/// A validator with no inspection capability — the shape a hand-rolled <c>IValidator</c> or a
/// non-FluentValidation implementation presents. It answers valid for everything: what these
/// tests read from it is the absence of the capability, not a verdict.
/// </summary>
public sealed class OpaqueValidator : IModelValidator<MarkerModel>
{
    public Task<ValidationReport> ValidateAsync(
        MarkerModel model,
        ValidationProfile profile,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Validate(model, profile));

    public ValidationReport Validate(MarkerModel model, ValidationProfile profile) => new([]);
}

/// <summary>
/// Forwards to a real adapter and counts how often the declared-path enumeration is asked for —
/// the read that walks the validator's declared rules, and the one a per-field-per-render
/// implementation would make once per bound component per render.
/// </summary>
public sealed class CountingInspector(FluentValidationModelValidator<MarkerModel> inner)
    : IModelValidator<MarkerModel>, IRuleInspectingValidator<MarkerModel>
{
    public int RuleMapReads { get; private set; }

    public bool CanInspectRules => inner.CanInspectRules;

    public Task<ValidationReport> ValidateAsync(
        MarkerModel model,
        ValidationProfile profile,
        CancellationToken cancellationToken = default) =>
        inner.ValidateAsync(model, profile, cancellationToken);

    public ValidationReport Validate(MarkerModel model, ValidationProfile profile) =>
        inner.Validate(model, profile);

    public FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile) =>
        inner.GetFieldRequirement(fieldPath, profile);

    public IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile)
    {
        RuleMapReads++;
        return inner.GetDeclaredFieldPaths(profile);
    }
}
