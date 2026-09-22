using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableFieldAnchorTests : BunitContext
{
    public FormidableFieldAnchorTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Fact]
    public void Field_id_is_deterministic_and_instance_scoped()
    {
        var a = new EngineItem();
        var b = new EngineItem();

        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(a, "Sku")), FormidableFieldId.For(new FieldIdentifier(a, "Sku")));
        Assert.NotEqual(FormidableFieldId.For(new FieldIdentifier(a, "Sku")), FormidableFieldId.For(new FieldIdentifier(b, "Sku")));
    }

    // The sanitizer's two clauses — lowercase the letters and digits, replace everything else
    // with `-` — are otherwise unexercised at unit level, because every other test uses an
    // already-lowercase alphanumeric name. `Location.X` runs both at once. These are EndsWith
    // rather than Equal on purpose: the sanitized name is the id's SUFFIX, which is the only
    // part a consumer's CSS or a browser locator can address, and moving it off the end would
    // break every `[id$='-...']` selector in the sample's own suite.
    [Fact]
    public void Field_id_sanitizes_the_name_into_its_suffix()
    {
        var a = new EngineItem();

        Assert.EndsWith("-location-x", FormidableFieldId.For(new FieldIdentifier(a, "Location.X")));
        Assert.EndsWith("-form", FormidableFieldId.For(new FieldIdentifier(a, string.Empty)));
    }

    // Two names that sanitize to the same suffix are still two fields, and two elements sharing a
    // DOM id is invalid HTML: both `aria-describedby` values point at one message list and a
    // focus move by that id reaches whichever element came first. The hash of the ORIGINAL name,
    // case and punctuation intact, is what separates them while the legible suffix stays shared.
    [Fact]
    public void Two_names_that_sanitize_alike_still_get_different_ids()
    {
        var a = new EngineItem();

        Assert.NotEqual(
            FormidableFieldId.For(new FieldIdentifier(a, "Url")),
            FormidableFieldId.For(new FieldIdentifier(a, "URL")));
        Assert.NotEqual(
            FormidableFieldId.For(new FieldIdentifier(a, "Location.X")),
            FormidableFieldId.For(new FieldIdentifier(a, "Location_X")));

        Assert.EndsWith("-url", FormidableFieldId.For(new FieldIdentifier(a, "Url")));
        Assert.EndsWith("-url", FormidableFieldId.For(new FieldIdentifier(a, "URL")));
    }

    // string.GetHashCode() is randomized per process, so an id built on it would differ between
    // one run of the app and the next — a stylesheet, a hand-written locator or a test computing
    // the expected id independently would all see a different answer. The library spells the hash
    // out instead. A literal is the only assertion that can tell a deterministic hash from a
    // per-process one from inside a single process.
    [Fact]
    public void The_name_hash_is_the_same_in_every_process()
    {
        var a = new EngineItem();

        Assert.Contains("-4e5768cb-", FormidableFieldId.For(new FieldIdentifier(a, "Description")));
        Assert.Contains("-811c9dc5-", FormidableFieldId.For(new FieldIdentifier(a, string.Empty)));
    }

    [Fact]
    public void Expression_overload_matches_the_string_path()
    {
        var order = new EngineOrder();

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            FormidableFieldId.For(order, o => o.Description));
    }

    // The object part is evaluated, the way FieldIdentifier.Create evaluates its own: the field a
    // component renders for `Nested.City` is owned by the Nested instance, so an id keyed on the
    // ROOT model names a field nothing renders — no element carries it, no message list matches
    // it, and a focus move by it finds nothing.
    [Fact]
    public void Expression_overload_names_the_field_the_nested_owner_owns()
    {
        var model = new IdShapes { Nested = new IdNested() };

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(model.Nested, nameof(IdNested.City))),
            FormidableFieldId.For(model, o => o.Nested!.City));
        Assert.NotEqual(
            FormidableFieldId.For(new FieldIdentifier(model, nameof(IdNested.City))),
            FormidableFieldId.For(model, o => o.Nested!.City));
    }

    // An object part that is not a member chain off the lambda's parameter — an indexer here — is
    // compiled rather than walked by reflection, and lands on the same row instance the row's own
    // components bind to.
    [Fact]
    public void Expression_overload_reaches_a_row_through_an_indexer()
    {
        var model = new IdShapes();
        model.Rows.Add(new IdNested());

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(model.Rows[0], nameof(IdNested.City))),
            FormidableFieldId.For(model, o => o.Rows[0].City));
    }

    // Only the boxing convert an Expression<Func<TModel, object>> puts over a VALUE-typed member
    // is read past. `!o.Flag` and `-o.Count` are UnaryExpressions too, and an operand-shaped test
    // accepts them and answers with the operand's name — an id for a field the caller never asked
    // for.
    [Fact]
    public void Expression_overload_reads_past_a_boxing_convert_and_nothing_else()
    {
        var model = new IdShapes();
        Expression<Func<IdShapes, object>> boxed = o => o.Count;

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(model, nameof(IdShapes.Count))),
            FormidableFieldId.For(model, boxed));

        Assert.Throws<ArgumentException>(() => FormidableFieldId.For(model, o => !o.Flag));
        Assert.Throws<ArgumentException>(() => FormidableFieldId.For(model, o => -o.Count));
    }

    // Reading through a null names a field that has no owner yet, and an id keyed on nothing
    // cannot be the one a component will render once the owner exists. FieldIdentifier.Create
    // refuses the same expression for the same reason.
    [Fact]
    public void Expression_overload_refuses_to_read_through_a_null()
    {
        var model = new IdShapes { Nested = null };

        Assert.Throws<ArgumentException>(() => FormidableFieldId.For(model, o => o.Nested!.City));
    }

    // The aria-describedby contract: the id an input points at and the id the message list renders
    // are the same string, and this is the one place that spells the suffix.
    [Fact]
    public void Messages_id_is_the_field_id_plus_the_suffix()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.Equal($"{FormidableFieldId.For(field)}-messages", FormidableFieldId.MessagesFor(field));
    }

    // Adaptation: bunit 2.9.0 does not dispose the first tree when a second top-level `Render`
    // call replaces it on the same BunitContext, so the anchor is rendered behind a bool flag
    // component parameter and flipped via re-parameterization
    // (`IRenderedComponent<T>.Render(...)`, the v2 equivalent of SetParametersAndRender) instead of
    // replacing the whole tree. The assertions are unchanged: registered on initialization,
    // unregistered on dispose.
    [Fact]
    public void Anchor_registers_and_unregisters_with_the_form_registry()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<AnchorHost>(0);
            builder.AddComponentParameter(1, nameof(AnchorHost.Order), order);
            builder.AddComponentParameter(2, nameof(AnchorHost.ShowAnchor), true);
            builder.CloseComponent();
        });

        var host = cut.FindComponent<AnchorHost>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Description))));

        host.Render(parameters => parameters.Add(p => p.ShowAnchor, false)); // re-parameterize -> anchor leaves the tree and disposes
        Assert.False(form.Instance.Engine!.Registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    // This deliberately does NOT route through FormidableForm/EditForm. EditForm tears down and
    // recreates its entire descendant subtree whenever its EditContext instance changes (it opens
    // a render region keyed on `_editContext.GetHashCode()` specifically so its internal
    // `CascadingValue<EditContext> IsFixed="true"` is safe — see EditForm.BuildRenderTree). That
    // means a FormidableFieldAnchor nested inside a FormidableForm/FormidableValidator-wrapped
    // EditForm is ALWAYS disposed and freshly constructed on a model swap, regardless of whether
    // FormidableFieldAnchor itself rebinds on cascading-parameter changes — so a test built on
    // top of FormidableForm cannot distinguish fixed from unfixed FormidableFieldAnchor code.
    // Cascading a FormidableFormContext directly (no EditForm underneath) isolates
    // FormidableFieldAnchor's own contract: it must rebind when
    // the cascaded context instance changes, independent of whatever caused that change.
    [Fact]
    public void Anchor_rebinds_registration_when_the_cascaded_context_is_replaced()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        RenderFragment fieldFragment = inner =>
        {
            inner.OpenComponent<FormidableFieldAnchor<string>>(0);
            inner.AddComponentParameter(1, nameof(FormidableFieldAnchor<string>.For), (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        };

        var firstEngine = CreateEngine(order);
        var firstContext = new FormidableFormContext(firstEngine);
        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadedContextHost>(0);
            builder.AddComponentParameter(1, nameof(CascadedContextHost.Context), firstContext);
            builder.AddComponentParameter(2, nameof(CascadedContextHost.ChildContent), fieldFragment);
            builder.CloseComponent();
        });

        Assert.True(firstEngine.Registry.IsRegistered(field));

        var secondEngine = CreateEngine(order);
        var secondContext = new FormidableFormContext(secondEngine);
        var host = cut.FindComponent<CascadedContextHost>();
        host.Render(parameters =>
        {
            parameters.Add(p => p.Context, secondContext);
            parameters.Add(p => p.ChildContent, fieldFragment);
        });

        Assert.True(secondEngine.Registry.IsRegistered(field));
        Assert.False(firstEngine.Registry.IsRegistered(field));
    }

    // The anchor renders nothing, so it deliberately never subscribes to the engine's StateChanged:
    // a validation pass leaves it with nothing to re-render, and a form holding one anchor per row
    // of a collection would otherwise schedule a render per anchor per pass. The message list
    // beside it — disclosing the error for the field the notification engaged — is the same
    // pass's proof that the notification did fire.
    [Fact]
    public void Anchor_does_not_re_render_when_a_validation_pass_lands()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        RenderFragment fragment = inner =>
        {
            inner.OpenComponent<FormidableFieldAnchor<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
            inner.OpenComponent<FormidableFieldMessage<string>>(2);
            inner.AddComponentParameter(3, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        };

        var engine = CreateEngine(order);
        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadedContextHost>(0);
            builder.AddComponentParameter(1, nameof(CascadedContextHost.Context), new FormidableFormContext(engine));
            builder.AddComponentParameter(2, nameof(CascadedContextHost.ChildContent), fragment);
            builder.CloseComponent();
        });

        var anchor = cut.FindComponent<FormidableFieldAnchor<string>>();
        var rendersBeforeThePass = anchor.RenderCount;

        order.Description = new string('x', 11); // past the draft rule's maximum length
        cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(field));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("li.formidable-message--error")));
        Assert.Equal(rendersBeforeThePass, anchor.RenderCount);
    }

    private static FormidableEngine<EngineOrder> CreateEngine(EngineOrder order) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions());

    [Fact]
    public void Anchor_outside_a_form_throws_clearly()
    {
        var order = new EngineOrder();
        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableFieldAnchor<string>>(0);
            builder.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            builder.CloseComponent();
        }));

        Assert.Contains("FormidableForm", exception.Message);
    }

    /// <summary>Test-only host so the anchor's presence can be toggled via a parameter re-render (see adaptation note above).</summary>
    private sealed class AnchorHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool ShowAnchor { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                if (ShowAnchor)
                {
                    inner.OpenComponent<FormidableFieldAnchor<string>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableFieldAnchor<string>.For), (System.Linq.Expressions.Expression<Func<string>>)(() => Order.Description));
                    inner.CloseComponent();
                }
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only model for the shapes the expression overload must tell apart: a nested owner, a row behind an indexer, a value-typed member, a bool.</summary>
    private sealed class IdShapes
    {
        public IdNested? Nested { get; set; }

        public List<IdNested> Rows { get; } = [];

        public int Count { get; set; }

        public bool Flag { get; set; }
    }

    /// <summary>The nested owner in <see cref="IdShapes"/>.</summary>
    private sealed class IdNested
    {
        public string? City { get; set; }
    }

    /// <summary>Test-only host that cascades a FormidableFormContext directly, with no EditForm underneath (see the rebind test's remarks).</summary>
    private sealed class CascadedContextHost : ComponentBase
    {
        [Parameter]
        public FormidableFormContext? Context { get; set; }

        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }
}
