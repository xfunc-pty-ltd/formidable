using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineServerIssueTests
{
    private readonly EngineOrder _order = new() { Description = "ok", Customer = new EngineCustomer() };
    private readonly EditContext _editContext;
    private readonly FakeTimeProvider _time = new();
    private readonly FormValidationEngine<EngineOrder> _engine;

    public FormValidationEngineServerIssueTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormValidationEngine<EngineOrder>(
            _order, _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), _time);
    }

    [Fact]
    public void Server_issues_land_inline_on_unregistered_fields_too()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);

        Assert.Contains(
            "Server rejected this description",
            _editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Model_level_server_issue_lands_at_form_level()
    {
        _engine.ApplyServerIssues([new ValidationIssue(string.Empty, "Duplicate submission")]);

        Assert.Contains("Duplicate submission", _editContext.GetValidationMessages(new FieldIdentifier(_order, string.Empty)));
    }

    [Fact]
    public void Warning_severity_server_issues_do_not_block_or_write_messages()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "advisory", ValidationSeverity.Warning)]);

        Assert.Empty(_editContext.GetValidationMessages());
    }

    [Fact]
    public async Task Fixing_the_field_clears_the_server_issue_via_refresh()
    {
        _order.Description = string.Empty; // also fails client submit rules
        _engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.NotEmpty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));

        _order.Description = "ok"; // fix
        _editContext.NotifyFieldChanged(new FieldIdentifier(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Empty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Disclosure_override_false_suppresses_server_issue()
    {
        using var engine = new FormValidationEngine<EngineOrder>(
            _order, new EditContext(_order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => false }, _time);

        engine.ApplyServerIssues([new ValidationIssue("Description", "suppressed")]);

        Assert.Empty(engine.EditContext.GetValidationMessages());
    }

    [Fact]
    public void Disclosure_override_true_keeps_server_issue()
    {
        using var engine = new FormValidationEngine<EngineOrder>(
            _order, new EditContext(_order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);

        engine.ApplyServerIssues([new ValidationIssue("Description", "kept")]);

        Assert.Contains("kept", engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    // A LINQ projection off a deserialized response body is the shape a caller actually holds, so
    // the parameter takes any sequence rather than charging a ToList() for the privilege. The
    // count matters as much as the acceptance: the payload is walked once, so an expensive or
    // single-pass sequence is safe to hand over.
    [Fact]
    public void A_lazy_sequence_is_accepted_and_walked_once()
    {
        var walks = 0;
        IEnumerable<ValidationIssue> Lazy()
        {
            walks++;
            yield return new ValidationIssue("Description", "Server rejected this description");
        }

        _engine.ApplyServerIssues(Lazy());

        Assert.Equal(1, walks);
        Assert.Contains(
            "Server rejected this description",
            _editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Same_field_issues_replace_across_calls()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "first")]);
        _engine.ApplyServerIssues([new ValidationIssue("Description", "second")]);

        var messages = _engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))).ToList();
        Assert.DoesNotContain("first", messages);
        Assert.Contains("second", messages);
    }

    [Fact]
    public void Applying_the_same_server_payload_twice_does_not_duplicate()
    {
        var payload = new[] { new ValidationIssue("Description", "Server rejected this description") };
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));

        _engine.ApplyServerIssues(payload);
        _engine.ApplyServerIssues(payload);

        Assert.Equal(1, _engine.GetIssues(description).Count(i => i.Message == "Server rejected this description"));
        Assert.Single(_editContext.GetValidationMessages(description));
    }

    [Fact]
    public void A_new_server_payload_replaces_the_previous_verdict()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customer = new FieldIdentifier(_order, nameof(EngineOrder.Customer));

        _engine.ApplyServerIssues([new ValidationIssue("Description", "Description rejected")]);
        _engine.ApplyServerIssues([new ValidationIssue("Customer", "Customer rejected")]);

        Assert.Empty(_engine.GetIssues(description));
        Assert.Contains(_engine.GetIssues(customer), i => i.Message == "Customer rejected");
    }

    [Fact]
    public async Task Reapplied_server_issue_is_not_duplicated_by_an_intervening_refresh()
    {
        // Walkthrough repro: apply a server issue, edit the field (arms the debounced
        // refresh, which clears the server source while the client pass re-produces an
        // equal issue of its own), let the refresh run, apply the same server verdict
        // again. Replace-per-apply must leave exactly ONE issue on the field - the
        // re-applied copy folds into the client's identical one instead of joining it.
        _order.Items = [new EngineItem(), new EngineItem()];
        var item0Sku = new FieldIdentifier(_order.Items[0], nameof(EngineItem.Sku));
        var item1Sku = new FieldIdentifier(_order.Items[1], nameof(EngineItem.Sku));

        // 1 & 2. Apply the server's first-submit verdict: both items are missing a SKU.
        _engine.ApplyServerIssues(
        [
            new ValidationIssue("Items[0].Sku", "SKU is required"),
            new ValidationIssue("Items[1].Sku", "SKU is required"),
        ]);

        // 3. Fix item 0 and let the debounced refresh (client-side, same message) run to
        //    completion before the next "send to server" click.
        _order.Items[0].Sku = "ABC";
        _editContext.NotifyFieldChanged(item0Sku);
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        // 4. The server re-validates and re-sends its current verdict: only item 1 still fails.
        _engine.ApplyServerIssues([new ValidationIssue("Items[1].Sku", "SKU is required")]);

        // 5. Exactly one issue on the surviving field - no duplicate from the refresh.
        Assert.Equal(1, _engine.GetIssues(item1Sku).Count(i => i.Message == "SKU is required"));
        Assert.Single(_engine.GetVisibleIssues(), v => v.Field.Equals(item1Sku) && v.Issue.Message == "SKU is required");
    }

    [Fact]
    public async Task Fixed_then_rebroken_field_does_not_duplicate_the_server_issue()
    {
        // The sibling the repro above never walks: a field fixed long enough for a refresh to
        // clear the server source while the field itself is clean, then re-broken so the
        // ordinary client-side pass reproduces an identical issue on a still-revealed field.
        // The next server apply must not put a second copy of that message beside it.
        _order.Items = [new EngineItem()];
        var sku = new FieldIdentifier(_order.Items[0], nameof(EngineItem.Sku));

        // 1. Apply the server's first-submit verdict: the item is missing a SKU.
        _engine.ApplyServerIssues([new ValidationIssue("Items[0].Sku", "SKU is required")]);
        Assert.Equal(1, _engine.GetIssues(sku).Count(i => i.Message == "SKU is required"));

        // 2. Fix it and let the debounced refresh run. The refresh clears the server source and
        //    the client's own answer for the field is clean - the field reading empty is what
        //    proves the server's copy is genuinely gone here, not merely shadowed.
        _order.Items[0].Sku = "ABC";
        _editContext.NotifyFieldChanged(sku);
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();
        Assert.Empty(_engine.GetIssues(sku));

        // 3. Break it again. Nothing here has un-revealed the field - only a passing submit
        //    does that, and none has run - so it is still a disclosed error site: the refresh's
        //    own client-side pass re-produces "SKU is required" as the client's own answer, with
        //    no server copy behind it. Exactly one copy - the client's.
        _order.Items[0].Sku = string.Empty;
        _editContext.NotifyFieldChanged(sku);
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();
        Assert.Equal(1, _engine.GetIssues(sku).Count(i => i.Message == "SKU is required"));

        // 4. The server re-sends the same verdict it always held for this field.
        _engine.ApplyServerIssues([new ValidationIssue("Items[0].Sku", "SKU is required")]);

        // 5. Still exactly one copy - the client merges first, so the server's identical twin
        //    folds into the client's copy instead of showing beside it.
        Assert.Equal(1, _engine.GetIssues(sku).Count(i => i.Message == "SKU is required"));
        Assert.Single(_engine.GetVisibleIssues(), v => v.Field.Equals(sku) && v.Issue.Message == "SKU is required");
    }

    [Fact]
    public async Task Client_submit_issues_survive_a_server_replace()
    {
        var order = new EngineOrder { Description = string.Empty, Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();
        var clientMessage = Assert.Single(editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.Contains("Server rejected this description", editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([]);

        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.Contains(clientMessage, messages);
        Assert.DoesNotContain("Server rejected this description", messages);
    }

    [Fact]
    public async Task Client_submit_issue_survives_a_server_replace_across_an_intervening_refresh()
    {
        // Same shape as Client_submit_issues_survive_a_server_replace, but with a debounced
        // refresh landing between the two ApplyServerIssues calls (an unrelated field's edit
        // arms it). The refresh re-derives Description's own still-failing client issue while
        // clearing the server source. The client's answer and the server's live in separate
        // sources, so nothing can mistake the client issue for "the server's" and let the
        // second (empty) apply delete it - exactly the regression this pin guards against.
        var order = new EngineOrder { Description = string.Empty, Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();
        var clientMessage = Assert.Single(editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.Contains("Server rejected this description", editContext.GetValidationMessages(description));

        // Unrelated field edit arms the debounced refresh; Description itself is untouched and
        // still fails its own client rule (still empty) the whole time.
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        engine.ApplyServerIssues([]);

        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.Contains(clientMessage, messages);
        Assert.DoesNotContain("Server rejected this description", messages);
    }

    [Fact]
    public void Server_advisories_land_at_the_severity_they_carry()
    {
        using var engine = DisclosedEngine(out var editContext, out var order);

        engine.ApplyServerIssues(
        [
            new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning),
            new ValidationIssue("Customer", "This customer was created today", ValidationSeverity.Info),
        ]);

        Assert.Contains(
            engine.GetIssues(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            i => i.Message == "Server prefers short references" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(
            engine.GetIssues(new FieldIdentifier(order, nameof(EngineOrder.Customer))),
            i => i.Message == "This customer was created today" && i.Severity == ValidationSeverity.Info);

        // The store is the EditContext interop surface a native ValidationMessage renders straight
        // out, and it carries error severity only. An applied advisory shows through Formidable's
        // own reads without ever becoming a native validation message.
        Assert.Empty(editContext.GetValidationMessages());
    }

    [Fact]
    public void An_advisory_only_payload_is_still_a_disclosure_event()
    {
        using var engine = DisclosedEngine(out _, out _);

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning)]);

        Assert.True(engine.HasSubmitted);
    }

    [Fact]
    public async Task A_server_advisory_the_client_already_shows_is_shown_once()
    {
        using var engine = DisclosedEngine(out _, out var order);
        order.Description = "a-b"; // passes NotEmpty, fails the Warning no-hyphen rule
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // The client's own submit discloses the hyphen warning first.
        await engine.ValidateForSubmitAsync();
        Assert.Single(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        // The server ran the same validator, so its response carries that warning word for word -
        // alongside one only the server could know.
        engine.ApplyServerIssues(
        [
            new ValidationIssue("Description", "Avoid hyphens", ValidationSeverity.Warning, Code: "server"),
            new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning),
        ]);

        var issues = engine.GetIssues(description);
        Assert.Contains(issues, i => i.Message == "Server prefers short references");

        // Once, and the surviving copy is the client's: the shadow rule keeps the first issue
        // showing for a field and drops the later repeat, and an applied payload lands after
        // whatever the submit already disclosed.
        var shown = Assert.Single(issues, i => i.Message == "Avoid hyphens");
        Assert.NotEqual("server", shown.Code);
        Assert.Single(engine.GetVisibleIssues(), v => v.Issue.Message == "Avoid hyphens");
    }

    [Fact]
    public async Task A_reapplied_server_advisory_is_not_duplicated_by_an_intervening_refresh()
    {
        // The advisory channel's half of the replace-per-apply contract, in the shape the error
        // channel's own pin above uses: apply, edit (arming the debounced refresh, which clears
        // the server source while the client pass re-produces an equal advisory of its own),
        // let the refresh run, apply the same verdict again. Exactly one of each message survives.
        using var engine = DisclosedEngine(out _, out var order);
        order.Description = "a-b";
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();

        ValidationIssue[] payload =
        [
            new ValidationIssue("Description", "Avoid hyphens", ValidationSeverity.Warning),
            new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning),
        ];

        engine.ApplyServerIssues(payload);

        engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        engine.ApplyServerIssues(payload);

        Assert.Single(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
        Assert.Single(engine.GetIssues(description), i => i.Message == "Server prefers short references");
        Assert.Single(engine.GetVisibleIssues(), v => v.Issue.Message == "Server prefers short references");
    }

    [Fact]
    public async Task A_client_advisory_survives_a_server_replace_across_an_intervening_refresh()
    {
        // The other direction, and the one the error channel learned the hard way: a field can
        // carry a server-applied advisory and an independently-failing client advisory at once.
        // The two live in separate sources, so clearing the server's contribution can never take
        // the client's own advisory with it.
        using var engine = DisclosedEngine(out _, out var order);
        order.Description = "a-b";
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();
        Assert.Single(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning)]);
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Server prefers short references");

        // An unrelated field's edit arms the debounced refresh; Description is untouched and still
        // fails its own client warning rule the whole time.
        engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        engine.ApplyServerIssues([]);

        var issues = engine.GetIssues(description);
        Assert.Contains(issues, i => i.Message == "Avoid hyphens");
        Assert.DoesNotContain(issues, i => i.Message == "Server prefers short references");
    }

    [Fact]
    public async Task A_server_advisory_replaces_only_its_own_severity_across_a_refresh()
    {
        // A message alone does not identify an advisory: the channel holds every non-error severity
        // in one list, so a server Info and a client Warning can carry the same sentence. The
        // views' client-wins collapse has to match on both, or the client's Warning would swallow
        // the server's Info as a duplicate - or a clearing apply take the Warning as the server's.
        using var engine = DisclosedEngine(out _, out var order);
        order.Description = "a-b"; // fails the client's Warning-severity no-hyphen rule
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();

        // The same sentence at the other severity.
        ValidationIssue[] payload = [new ValidationIssue("Description", "Avoid hyphens", ValidationSeverity.Info)];
        engine.ApplyServerIssues(payload);

        engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        engine.ApplyServerIssues(payload);

        // Both survive, each at its own severity. The shadow rule shows the first copy only, and
        // that copy is the client's Warning — disclosed at submit, and never taken away.
        var shown = Assert.Single(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
        Assert.Equal(ValidationSeverity.Warning, shown.Severity);
        Assert.True(engine.GetFieldState(description).HasWarnings);

        // The server's Info is genuinely behind it, still the server source's own entry: clearing
        // the server verdict takes that copy and leaves the client's Warning standing.
        engine.ApplyServerIssues([]);
        Assert.Equal(
            ValidationSeverity.Warning,
            Assert.Single(engine.GetIssues(description), i => i.Message == "Avoid hyphens").Severity);
    }

    [Fact]
    public void A_server_advisory_with_nowhere_to_show_is_suppressed_and_reported()
    {
        var suppressed = new List<ValidationIssue>();
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { SuppressedIssueDiagnostic = suppressed.Add }, _time);

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server prefers short references", ValidationSeverity.Warning)]);

        // Nothing rendered a field for Description, so the advisory has nowhere to land: it is not
        // shown, the diagnostic names it, and no defensive gate stands in for it - an advisory
        // blocks nothing, so a hidden one has nothing to block.
        Assert.Empty(engine.GetIssues(new FieldIdentifier(order, nameof(EngineOrder.Description))));
        Assert.Empty(engine.GetIssues(new FieldIdentifier(order, string.Empty)));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Contains(suppressed, i => i.Message == "Server prefers short references");
    }

    /// <summary>
    /// An engine over its own model whose disclosure override forces every issue visible — the
    /// engine-level stand-in for a page that renders, and so registers, each field under test.
    /// </summary>
    private FormValidationEngine<EngineOrder> DisclosedEngine(out EditContext editContext, out EngineOrder order)
    {
        order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        editContext = new EditContext(order);
        return new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);
    }
}
