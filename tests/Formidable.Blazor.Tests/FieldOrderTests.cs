using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Issue order is what a blocked submit's auto-focus and the summary's first entry both read, so
/// it has to follow the fields on the page rather than the order the validator happens to declare
/// its rules in.
/// </summary>
public class FieldOrderTests : BunitContext
{
    private readonly EngineOrder _order = new() { Customer = new EngineCustomer() };
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly FakeTimeProvider _time = new();

    public FieldOrderTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order,
            _editContext,
            new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            _time);
    }

    [Fact]
    public async Task Visible_issues_follow_the_supplied_field_order_not_validator_order()
    {
        // DeclarationOrderValidator declares Description before Customer.Name; hand the engine
        // the opposite order and the summary must follow the page, not the validator.
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));

        _engine.Registry.Register(description);
        _engine.Registry.Register(customerName);
        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [customerName] = 0,
            [description] = 1,
        });

        await _engine.ValidateForSubmitAsync();

        var fields = _engine.GetVisibleIssues().Select(v => v.Field).ToList();
        Assert.Equal(customerName, fields[0]);
        Assert.Contains(description, fields);
        Assert.True(fields.IndexOf(customerName) < fields.IndexOf(description));
    }

    [Fact]
    public async Task Fields_absent_from_the_order_map_sort_last()
    {
        // The validator's form-level rule fails first and is visible without any registration, so
        // it is the unmapped field standing behind the mapped one.
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);
        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 5 });

        await _engine.ValidateForSubmitAsync();

        var fields = _engine.GetVisibleIssues().Select(v => v.Field).ToList();
        Assert.Equal(description, fields[0]); // everything unmapped follows it
        Assert.Equal(_engine.ModelLevelField, fields[^1]);
    }

    [Fact]
    public async Task Two_issues_on_one_field_keep_validator_order()
    {
        // Ordering must be a STABLE sort: a field with two issues keeps the order the
        // validator produced them in.
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);
        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 });

        await _engine.ValidateForSubmitAsync();

        var messages = _engine.GetVisibleIssues()
            .Where(v => v.Field.Equals(description))
            .Select(v => v.Issue.Message)
            .ToList();
        var unordered = _engine.GetIssues(description).Select(i => i.Message).ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal(unordered, messages);
    }

    [Fact]
    public async Task No_order_map_leaves_issues_in_validator_order()
    {
        // The contract before the host's first resolve lands, and the contract for a host that
        // never resolves one at all.
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));
        _engine.Registry.Register(description);
        _engine.Registry.Register(customerName);

        await _engine.ValidateForSubmitAsync();

        var fields = _engine.GetVisibleIssues().Select(v => v.Field).ToList();
        Assert.Equal(_engine.ModelLevelField, fields[0]);
        Assert.Equal(customerName, fields[^1]);
    }

    // The form is what turns a rendered page into the map above: it asks the order service where
    // the fields it has registered actually are, and hands the answer to its engine. The service
    // here answers with the page's order, which is the reverse of the declaration order the
    // validator's messages would otherwise arrive in.
    [Fact]
    public async Task The_form_hands_its_engine_the_order_the_service_resolved()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(DocumentOrderMessages, SummaryEntries(cut)));

        var requested = Assert.Single(fieldOrder.Requests);
        Assert.Contains(DescriptionField(order), requested);
        Assert.Contains(CustomerNameField(order), requested);

        await Services.DisposeAsync();
    }

    // What the seam is handed is the field itself, not the id of the element rendering it. An id
    // is derived from a field and cannot be read back into one, so an implementation given ids
    // could only ever sort by the DOM; given fields it can sort by anything it knows about them.
    [Fact]
    public async Task The_order_service_is_asked_about_fields_not_element_ids()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService();
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.WaitForAssertion(() => Assert.NotEmpty(fieldOrder.Requests));

        // FieldIdentifier equality is reference equality on the owning instance, so matching these
        // is the whole claim: the nested field arrives owned by the customer object it belongs to,
        // which its element id — a hash of that instance and a name — only ever points away from.
        var requested = Assert.Single(fieldOrder.Requests);
        Assert.Equal(3, requested.Count); // the two registered fields plus the model-level one
        Assert.Contains(DescriptionField(order), requested);
        Assert.Contains(CustomerNameField(order), requested);

        await Services.DisposeAsync();
    }

    // The model-level field rides on the <form> element itself, which contains every other field
    // on the page — so it belongs in the request the same as any registered field, not bolted on
    // after the fact. compareDocumentPosition is what turns that containment into "sorts first".
    [Fact]
    public async Task The_model_level_field_is_offered_to_the_order_service()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService();
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.WaitForAssertion(() => Assert.NotEmpty(fieldOrder.Requests));

        var requested = Assert.Single(fieldOrder.Requests);
        Assert.Equal(3, requested.Count); // the two registered fields plus the model-level one
        var modelLevel = Assert.Single(requested, field => field.FieldName == string.Empty);
        Assert.Same(order, modelLevel.Model);

        await Services.DisposeAsync();
    }

    // Being IN the request is not the same as sorting first — that only shows up once the
    // model-level field's issue coexists with another visible one. The all-suppressed gate can
    // never demonstrate this: by definition it is the form's only visible issue. A live-pass
    // fault can, because it does not clear whatever a prior submit already made visible - so this
    // stages a submit's field error, then a live-pass fault on top of it, and checks the sort.
    [Fact]
    public async Task The_model_level_field_sorts_first_among_coexisting_visible_issues()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var validator = new FaultAndFieldErrorValidator();
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        engine.Registry.Register(description);

        // Description is empty, so the Submit ruleset's error is visible once submitted.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));

        // A live pass on the same field now faults - landing the model-level issue without
        // touching the submit error still showing from the pass above.
        validator.Throw = true;
        editContext.NotifyFieldChanged(description);
        await Task.Yield();

        var beforeOrdering = engine.GetVisibleIssues().Select(v => v.Field).ToList();
        Assert.Contains(engine.ModelLevelField, beforeOrdering);
        Assert.Contains(description, beforeOrdering); // both still visible - the coexistence itself

        engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [engine.ModelLevelField] = 0,
            [description] = 1,
        });

        var fields = engine.GetVisibleIssues().Select(v => v.Field).ToList();
        Assert.Equal(engine.ModelLevelField, fields[0]);
        Assert.True(fields.IndexOf(engine.ModelLevelField) < fields.IndexOf(description));
    }

    // The rendered counterpart of the test above, and the whole loop it depends on: the form puts
    // the model-level field in the request, the service answers with it somewhere in the order,
    // and the ordinal map the form builds from that answer is what the engine sorts by. The
    // recording service answers with the request reversed, and the form appends the model-level
    // field last — so it comes back FIRST here, in front of two coexisting field errors. Leave it
    // out of the request and it would carry no ordinal at all, which sorts it to the end.
    [Fact]
    public async Task A_rendered_form_sorts_the_model_level_field_by_its_resolved_ordinal()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService();
        WireHost(fieldOrder);
        var module = SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        var modelLevel = new FieldIdentifier(order, string.Empty);
        var visible = cut.Instance.Engine!.GetVisibleIssues();

        // Coexistence first — a model-level issue alone would make "first" say nothing.
        Assert.Contains(visible, v => v.Field.Equals(DescriptionField(order)));
        Assert.Contains(visible, v => v.Field.Equals(CustomerNameField(order)));

        Assert.Equal(modelLevel, visible[0].Field);
        Assert.Equal("The order is incomplete", visible[0].Issue.Message);
        Assert.Equal("The order is incomplete", SummaryEntries(cut)[0]);

        // And the blocked submit takes the visitor to it: the id is the one the <form> carries.
        Assert.Equal(
            FormidableFieldId.For(modelLevel),
            module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // Document order decides which field is FIRST, but not which issue a blocked submit takes the
    // visitor to. A page whose top field carries only a warning, with the error below it, must
    // still land on the error — otherwise focus and the summary (which regroups by severity, so it
    // leads with the error regardless) point at two different fields.
    [Fact]
    public async Task Blocked_submit_focuses_the_first_error_not_an_advisory_above_it()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [DescriptionField(order), CustomerNameField(order)],
        };
        WireHost(fieldOrder);
        var module = SetUpJsModule();

        var cut = RenderHostForm(order, new AdvisoryAboveErrorValidator());
        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        // The warning on Description is the first VISIBLE issue; the error on Customer.Name is the
        // first blocking one.
        Assert.Equal("Description could be clearer", SummaryEntries(cut)[^1]);
        Assert.Equal(CustomerNameId(order), module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // The version guard is what stops the form re-resolving on every render, so a resolve that
    // fails must not leave it latched: a stable form registers nothing further, so there would be
    // no later render carrying a retry, and the page would keep validator order for good. The JS
    // module deliberately drops a faulted import so the NEXT call re-imports — this is what makes
    // sure there is a next call.
    [Fact]
    public async Task A_faulted_resolve_is_retried_on_the_next_render()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
            Fault = new JSDisconnectedException("the circuit is gone"),
            FaultOnce = true,
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.Find("form").Submit();

        // The engine's own order is the contract; what is already on screen picks it up on its
        // next render, which every pass and every edit triggers anyway.
        cut.WaitForAssertion(() => Assert.Equal(
            DocumentOrderMessages,
            cut.Instance.Engine!.GetVisibleIssues().Select(v => v.Issue.Message).ToArray()));
        Assert.Equal(2, fieldOrder.Requests.Count); // the failure, then the retry that stuck

        await Services.DisposeAsync();
    }

    // The other half of that contract: a service that never answers costs the page its reading
    // order and nothing else. The form still renders and still submits, on the order it had.
    [Fact]
    public async Task A_faulting_order_service_leaves_the_form_rendering_in_the_order_it_had()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
            Fault = new JSDisconnectedException("the circuit is gone"),
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(
            [
                "The order is incomplete",
                "Description is required",
                "Description is too short",
                "Customer name is required",
            ],
            SummaryEntries(cut)));
        Assert.NotNull(cut.Find("form"));

        // Retrying per render is the point, but each retry rides a render something else caused —
        // nothing here re-renders on the failure path, so the count stays far below the ceiling
        // asserted here.
        Assert.InRange(fieldOrder.Requests.Count, 1, 20);

        await Services.DisposeAsync();
    }

    // The same latch contract, for the seam's other way of not answering. An order that could not
    // be resolved at all is not a page that placed none of these fields, and latching the version
    // guard on one would strand a stable form on validator order exactly as a latched fault would.
    [Fact]
    public async Task A_resolve_that_answered_with_nothing_is_retried_on_the_next_render()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
            AnswerWithNothingOnce = true,
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(
            DocumentOrderMessages,
            cut.Instance.Engine!.GetVisibleIssues().Select(v => v.Issue.Message).ToArray()));
        Assert.Equal(2, fieldOrder.Requests.Count); // the empty-handed answer, then the retry

        await Services.DisposeAsync();
    }

    // FormidableOptions.OrderIssues is a pipeline stage over the order the service resolved, not
    // a competitor to it: unset, the resolved document order is untouched.
    [Fact]
    public async Task A_null_order_delegate_leaves_the_resolved_document_order_untouched()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator(), new FormidableOptions());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(DocumentOrderMessages, SummaryEntries(cut)));

        // The ordinal map itself, not just the message text it produces: the two registered
        // fields appear in exactly the order the service answered with.
        var fields = cut.Instance.Engine!.GetVisibleIssues()
            .Select(v => v.Field)
            .Where(field => field.Equals(CustomerNameField(order)) || field.Equals(DescriptionField(order)))
            .Distinct()
            .ToList();
        Assert.Equal(fieldOrder.Result, fields);

        await Services.DisposeAsync();
    }

    // A set delegate re-sorts what the service resolved. Reversing what the service answered
    // with is deliberately the opposite of reversing registration order — the two only agree if
    // the delegate is handed the service's own answer rather than the registry's raw order.
    [Fact]
    public async Task The_order_delegate_re_sorts_the_resolved_document_order()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
        };
        WireHost(fieldOrder);
        SetUpJsModule();

        IReadOnlyList<FieldIdentifier>? delegateInput = null;
        var options = new FormidableOptions
        {
            OrderIssues = fields =>
            {
                delegateInput = fields;
                return fields.Reverse().ToList();
            },
        };

        var cut = RenderHostForm(order, new DeclarationOrderValidator(), options);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(
            [
                "Description is required",
                "Description is too short",
                "Customer name is required",
                "The order is incomplete",
            ],
            SummaryEntries(cut)));

        // What the delegate actually saw was the service's resolved order, not the registry's —
        // the two are deliberately different here (registration order is Description then
        // Customer.Name; the service answers the reverse).
        Assert.Equal(fieldOrder.Result, delegateInput);

        await Services.DisposeAsync();
    }

    // The guarantee that matters most: a delegate reorders, it never suppresses. Renders nothing
    // — this pins the append behavior directly against the helper FormidableForm applies the
    // delegate through.
    [Fact]
    public void Fields_the_order_delegate_drops_are_appended_in_document_order()
    {
        var order = NewOrder();
        var description = DescriptionField(order);
        var customerName = CustomerNameField(order);
        var location = new FieldIdentifier(order, nameof(EngineOrder.Location));
        var resolved = new List<FieldIdentifier> { description, customerName, location };

        var result = FormidableForm<EngineOrder>.ApplyOrderDelegate(
            resolved,
            fields => [fields[1]]);

        Assert.Equal(new List<FieldIdentifier> { customerName, description, location }, result);
    }

    // The version guard latches BEFORE the await starts, which is exactly what makes an in-flight
    // resolve outlived by a newer one dangerous: on its own return it has no way to know a second
    // resolve was ever asked for, let alone that the second one already answered. Two overlapping
    // resolves are driven by hand through a service whose OrderAsync hands back a task the test
    // completes itself, so which one finishes last is chosen rather than raced — B (the later
    // resolve) is completed first, then A (the earlier one) last, and the map left in force must
    // still be B's.
    [Fact]
    public async Task An_order_resolve_that_completes_out_of_order_is_discarded()
    {
        var order = NewOrder();
        var fieldOrder = new StepFieldOrderService();
        Services.AddSingleton<IFormidableFieldOrderService>(fieldOrder);
        Services.AddFormidableBlazor();
        SetUpJsModule();

        var cut = Render(builder =>
        {
            builder.OpenComponent<OverlapHost>(0);
            builder.AddComponentParameter(1, nameof(OverlapHost.Order), order);
            builder.AddComponentParameter(2, nameof(OverlapHost.ShowThird), false);
            builder.CloseComponent();
        });

        var host = cut.FindComponent<OverlapHost>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        cut.WaitForAssertion(() => Assert.Single(fieldOrder.Calls)); // resolve A, from the initial render

        // Issues to reorder, staged while A is still unanswered.
        await cut.InvokeAsync(() => form.Instance.SubmitAsync());

        // Registering a second field bumps the registry version, which is what makes the form ask
        // the order service again rather than treating the render as one it has already resolved.
        host.Render(parameters => parameters.Add(p => p.ShowThird, true));
        cut.WaitForAssertion(() => Assert.Equal(2, fieldOrder.Calls.Count)); // resolve B

        var description = DescriptionField(order);
        var customerName = CustomerNameField(order);

        // B, the later resolve, completes first; A, the earlier one, completes last — the ordering
        // the property under test depends on.
        await cut.InvokeAsync(() => fieldOrder.Calls[1].SetResult([customerName, description]));
        await cut.InvokeAsync(() => fieldOrder.Calls[0].SetResult([description, customerName]));

        // A submit rather than a bare wait, so the assertion below reads a summary that has been
        // rebuilt since the last of the two answers landed, whichever of them won.
        await cut.InvokeAsync(() => form.Instance.SubmitAsync());

        cut.WaitForAssertion(() => Assert.Equal(DocumentOrderMessages, SummaryEntries(form)));
    }

    // The registry's version answers which fields exist, not where they sit. A keyed reorder moves
    // rendered elements without registering or unregistering anything, so a resolve gated on that
    // version alone never runs again and the summary keeps listing the page the way it used to be.
    [Fact]
    public async Task A_layout_move_re_resolves_the_order()
    {
        var order = NewOrder();
        var description = DescriptionField(order);
        var customerName = CustomerNameField(order);
        var fieldOrder = new RecordingFieldOrderService { Result = [customerName, description] };
        WireHost(fieldOrder);
        var module = SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(DocumentOrderMessages, SummaryEntries(cut)));

        var resolvesBeforeTheMove = fieldOrder.Requests.Count;

        // The page moves its fields. Nothing registers or unregisters, so the registry's version is
        // exactly the one the order above was resolved against.
        fieldOrder.Result = [description, customerName];
        // Driven through the very reference observeLayout was handed, rather than through a method
        // on the component: that reference is all the browser holds, so this also says the object
        // the script reports to is one whose callback actually runs.
        var receiver = (DotNetObjectReference<LayoutObserverReceiver>)
            module.Invocations["observeLayout"].Single().Arguments[1]!;
        await cut.InvokeAsync(() => receiver.Value.NotifyLayoutMoved());

        cut.WaitForAssertion(() => Assert.True(
            fieldOrder.Requests.Count > resolvesBeforeTheMove,
            "the layout move provoked no fresh order resolve"));

        // The same fields, asked about again: the second request is a question about where they
        // are, not about which of them exist.
        Assert.Equal(fieldOrder.Requests[resolvesBeforeTheMove - 1], fieldOrder.Requests[^1]);

        // Nothing further is driven from here on purpose. A move that registers nothing raises no
        // pass, no edit and no server apply, so if the resolved order did not put itself on screen
        // the summary would keep listing the page as it used to be — the reported bug exactly.
        cut.WaitForAssertion(() => Assert.Equal(
            [
                "Description is required",
                "Description is too short",
                "Customer name is required",
                "The order is incomplete",
            ],
            SummaryEntries(cut)));

        await Services.DisposeAsync();
    }

    // The observer lives in the browser and holds a reference back into the component, so a form
    // that goes away without disconnecting leaves the page reporting moves to something that is no
    // longer there.
    [Fact]
    public async Task Disposing_the_form_disconnects_the_layout_observer()
    {
        var order = NewOrder();
        var fieldOrder = new RecordingFieldOrderService
        {
            Result = [CustomerNameField(order), DescriptionField(order)],
        };
        WireHost(fieldOrder);
        var module = SetUpJsModule();

        var cut = RenderHostForm(order, new DeclarationOrderValidator());
        cut.WaitForAssertion(() => Assert.Contains("observeLayout", module.Invocations.Identifiers));

        await DisposeComponentsAsync();

        // Addressed by the form element's own id, the one the observer was established on.
        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, string.Empty)),
            module.Invocations["disconnectLayoutObserver"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // A resolve lands after the render that produced the elements it measured, so a changed order
    // has nothing else to arrive on: whatever reads issues has already rendered by then.
    [Fact]
    public void A_resolved_order_that_differs_notifies()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));
        var notifications = 0;
        _engine.StateChanged += (_, _) => notifications++;

        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [description] = 0,
            [customerName] = 1,
        });
        Assert.Equal(1, notifications); // validator order to a resolved one is a difference

        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [customerName] = 0,
            [description] = 1,
        });
        Assert.Equal(2, notifications);

        _engine.SetFieldOrder(null);
        Assert.Equal(3, notifications); // and back to validator order is one too
    }

    // The half that pins the gate. Most resolves answer with the order already in force — a
    // registration change usually leaves the reading order where it was, and a host watching the
    // page for moves resolves again on the very re-render a changed order provokes. Notifying on
    // those would put a render round behind every registration change, and would leave the second
    // of that pair provoking a third.
    [Fact]
    public void A_resolved_order_matching_the_one_in_force_notifies_nothing()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));
        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [description] = 0,
            [customerName] = 1,
        });

        var notifications = 0;
        _engine.StateChanged += (_, _) => notifications++;

        // A different instance carrying the same answer, which is what a fresh resolve hands over.
        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int>
        {
            [description] = 0,
            [customerName] = 1,
        });

        Assert.Equal(0, notifications);
    }

    /// <summary>The four messages DeclarationOrderValidator produces, in the page's order.</summary>
    private static string[] DocumentOrderMessages =>
    [
        "Customer name is required",
        "Description is required",
        "Description is too short",
        "The order is incomplete",
    ];

    private static EngineOrder NewOrder() => new() { Customer = new EngineCustomer() };

    private static FieldIdentifier DescriptionField(EngineOrder order) =>
        new(order, nameof(EngineOrder.Description));

    private static FieldIdentifier CustomerNameField(EngineOrder order) =>
        new(order.Customer!, nameof(EngineCustomer.Name));

    /// <summary>The element id the customer name field renders — what the focus service is given.</summary>
    private static string CustomerNameId(EngineOrder order) =>
        FormidableFieldId.For(CustomerNameField(order));

    private static string[] SummaryEntries(IRenderedComponent<FormidableForm<EngineOrder>> cut) =>
        cut.FindAll(".formidable-summary__link").Select(e => e.TextContent).ToArray();

    private void WireHost(RecordingFieldOrderService fieldOrder)
    {
        Services.AddSingleton<IFormidableFieldOrderService>(fieldOrder);
        Services.AddFormidableBlazor();
    }

    private BunitJSModuleInterop SetUpJsModule()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);

        // The form establishes its layout observer through the same module. Strict mode is what
        // makes planning it necessary at all: an unplanned invocation is a test-harness failure,
        // not the interop failure the form is written to tolerate.
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>
    /// A summary over two registered fields, Description above Customer.Name — the page order the
    /// order service's answers describe.
    /// </summary>
    private IRenderedComponent<FormidableForm<EngineOrder>> RenderHostForm(
        EngineOrder order, FluentValidation.IValidator<EngineOrder> validator, FormidableOptions? options = null) =>
        Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Validator),
                new FluentValidationModelValidator<EngineOrder>(validator));
            if (options is not null)
            {
                builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.Options), options);
            }

            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableSummary>(0);
                    inner.CloseComponent();
                    Input(inner, 1, () => order.Description, v => order.Description = v ?? string.Empty);
                    Input(inner, 5, () => order.Customer!.Name, v => order.Customer!.Name = v ?? string.Empty);
                    inner.AddMarkupContent(9, "<button type=\"submit\">Go</button>");
                }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>();

    private void Input(
        Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder,
        int sequence,
        Expression<Func<string?>> accessor,
        Action<string?> setter)
    {
        builder.OpenComponent<FormidableInputText>(sequence);
        builder.AddComponentParameter(sequence + 1, nameof(FormidableInputText.For), accessor);
        builder.AddComponentParameter(sequence + 2, "Value", accessor.Compile()());
        builder.AddComponentParameter(
            sequence + 3, "ValueChanged", EventCallback.Factory.Create(this, setter));
        builder.CloseComponent();
    }

    /// <summary>
    /// Answers <see cref="IFormidableFieldOrderService.OrderAsync"/> with a task the test completes
    /// by hand, one per call — so a test can choose which of two overlapping resolves finishes
    /// last instead of racing real ones.
    /// </summary>
    private sealed class StepFieldOrderService : IFormidableFieldOrderService
    {
        /// <summary>One entry per call, in call order, each still pending until the test completes it.</summary>
        public List<TaskCompletionSource<IReadOnlyList<FieldIdentifier>?>> Calls { get; } = [];

        public ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields)
        {
            var call = new TaskCompletionSource<IReadOnlyList<FieldIdentifier>?>();
            Calls.Add(call);
            return new ValueTask<IReadOnlyList<FieldIdentifier>?>(call.Task);
        }
    }

    /// <summary>
    /// Test-only host whose <see cref="ShowThird"/> toggles a second registration of the
    /// description field on and off, purely to bump the registry's version and provoke a second
    /// order resolve on the form beneath it — see
    /// <see cref="An_order_resolve_that_completes_out_of_order_is_discarded"/>.
    /// </summary>
    private sealed class OverlapHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool ShowThird { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Validator),
                new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()));
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(1);
                inner.AddComponentParameter(2, "For", (Expression<Func<string?>>)(() => Order.Description));
                inner.AddComponentParameter(3, "Value", Order.Description);
                inner.AddComponentParameter(
                    4, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Order.Description = v ?? string.Empty));
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(5);
                inner.AddComponentParameter(6, "For", (Expression<Func<string?>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(7, "Value", Order.Customer!.Name);
                inner.AddComponentParameter(
                    8, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Order.Customer!.Name = v ?? string.Empty));
                inner.CloseComponent();

                if (ShowThird)
                {
                    inner.OpenComponent<FormidableFieldAnchor<string>>(9);
                    inner.AddComponentParameter(
                        10, nameof(FormidableFieldAnchor<string>.For), (Expression<Func<string>>)(() => Order.Description));
                    inner.CloseComponent();
                }
            }));
            builder.CloseComponent();
        }
    }
}
