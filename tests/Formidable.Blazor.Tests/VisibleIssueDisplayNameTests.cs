using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the name every visible issue carries: the rule's display name, else the issue's path,
/// else the model-level name the options hold at the moment of the read. The submit outcome's
/// error summary names its entries by the same rule, so the name beside an entry and the name
/// the count line lists it under cannot disagree.
/// </summary>
public class VisibleIssueDisplayNameTests
{
    private const string ShippedModelLevelName = "This form";

    private const string ReVoicedModelLevelName = "Ce formulaire";

    private sealed class Ticket
    {
        public string Reference { get; set; } = string.Empty;

        public string Requester { get; set; } = string.Empty;

        public string Impact { get; set; } = string.Empty;
    }

    private sealed class TicketValidator : DraftSubmitValidator<Ticket>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Reference).NotEmpty().WithName("Ticket reference");
    }

    // A validator of the page's own rather than FluentValidation, which names every failure it
    // reports: only a hand-rolled validator (or a server reply) yields an issue with a path and
    // no name, and the agreement pin needs one among a submit's own errors.
    private sealed class FixedReportValidator(params ValidationIssue[] issues) : IModelValidator<Ticket>
    {
        public Task<ValidationReport> ValidateAsync(
            Ticket model,
            ValidationProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(Ticket model, ValidationProfile profile) => new(issues);
    }

    private static FormidableEngine<Ticket> Build(
        Ticket ticket,
        IModelValidator<Ticket> validator,
        FormidableOptions? options = null) =>
        new(
            ticket,
            new EditContext(ticket),
            validator,
            new ReflectionModelIntrospector(),
            options ?? new FormidableOptions(),
            new FakeTimeProvider());

    private static FormidableEngine<EngineOrder> BuildOrder(
        EngineOrder order,
        EditContext editContext,
        IValidator<EngineOrder> validator,
        FormidableOptions options) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    [Fact]
    public async Task A_field_issue_is_named_by_its_display_name()
    {
        // Mutation: the submit-error site in GetVisibleIssues builds its VisibleIssue without
        // the name, and the entry reads null.
        var ticket = new Ticket();
        using var engine = Build(ticket, new FluentValidationModelValidator<Ticket>(new TicketValidator()));
        using var reference = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Reference)));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var entry = Assert.Single(engine.GetVisibleIssues());
        Assert.Equal("Ticket reference", entry.Issue.DisplayName);
        Assert.Equal("Ticket reference", entry.DisplayName);
    }

    [Fact]
    public void An_issue_with_no_display_name_is_named_by_its_path()
    {
        // Mutation: drop the Path fallback in IssueDisplayName.Resolve, and the entry reads the
        // model-level name instead of "Reference".
        var ticket = new Ticket();
        using var engine = Build(ticket, new FluentValidationModelValidator<Ticket>(new TicketValidator()));
        using var reference = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Reference)));

        // A response-body error, as a ProblemDetails mapping produces it: a path and a message.
        engine.ApplyServerIssues([new ValidationIssue(nameof(Ticket.Reference), "That reference is already open")]);

        var entry = Assert.Single(engine.GetVisibleIssues());
        Assert.Null(entry.Issue.DisplayName);
        Assert.Equal(nameof(Ticket.Reference), entry.DisplayName);
    }

    [Fact]
    public async Task The_gate_and_the_fault_are_named_by_ModelLevelDisplayName()
    {
        // Mutation: IssueDisplayName.Resolve falls back to the shipped "This form" rather than
        // the name it is handed, and both entries read the shipped name.

        // The gate: every failing field is hidden, so the blocked submit can show only its
        // explanation, which names no field.
        var gatedOrder = new EngineOrder();
        using var gated = BuildOrder(
            gatedOrder,
            new EditContext(gatedOrder),
            new EngineOrderValidator(),
            new FormidableOptions { ModelLevelDisplayName = ReVoicedModelLevelName });

        Assert.False((await gated.ValidateForSubmitAsync()).CanProceed);

        var gate = Assert.Single(gated.GetVisibleIssues());
        Assert.Equal(string.Empty, gate.Field.FieldName);
        Assert.Equal(gated.Options.DefensiveGateMessage, gate.Issue.Message);
        Assert.Equal(ReVoicedModelLevelName, gate.DisplayName);

        // The fault: a live rule throws, and the form is left holding the fault message.
        var faultingOrder = new EngineOrder();
        var faultingContext = new EditContext(faultingOrder);
        using var faulted = BuildOrder(
            faultingOrder,
            faultingContext,
            new ThrowingValidator(),
            new FormidableOptions { ModelLevelDisplayName = ReVoicedModelLevelName });

        faultingContext.NotifyFieldChanged(new FieldIdentifier(faultingOrder, nameof(EngineOrder.Description)));
        await Task.Yield();

        var fault = Assert.Single(faulted.GetVisibleIssues(), v => v.Field.FieldName.Length == 0);
        Assert.Equal(faulted.Options.ValidationFaultMessage, fault.Issue.Message);
        Assert.Equal(ReVoicedModelLevelName, fault.DisplayName);
    }

    [Fact]
    public async Task A_changed_ModelLevelDisplayName_names_the_next_read()
    {
        // Mutation: the engine keeps the ModelLevelDisplayName it was built with and names every
        // later read from that copy, so the second read still says "This form".
        var options = new FormidableOptions();
        var order = new EngineOrder();
        using var engine = BuildOrder(order, new EditContext(order), new EngineOrderValidator(), options);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(ShippedModelLevelName, Assert.Single(engine.GetVisibleIssues()).DisplayName);

        // Nothing runs between the two reads: options change in place, and the next read
        // answers from them.
        options.ModelLevelDisplayName = ReVoicedModelLevelName;

        Assert.Equal(ReVoicedModelLevelName, Assert.Single(engine.GetVisibleIssues()).DisplayName);
    }

    [Fact]
    public async Task The_entry_name_and_VisibleErrorSummary_agree()
    {
        // Mutations: GetVisibleIssues names its entries by a copy of the rule with a different
        // fallback (DisplayName ?? Path, no model-level name), and the model-level entry reads ""
        // where the summary says "This form". Or drop the Path fallback in
        // IssueDisplayName.Resolve: both sides then call the Requester entry "This form" and
        // still agree, so the pin on the three names below is what catches it.
        var ticket = new Ticket();
        using var engine = Build(ticket, new FixedReportValidator(
            new ValidationIssue(nameof(Ticket.Reference), "A reference is required", DisplayName: "Ticket reference"),
            new ValidationIssue(nameof(Ticket.Requester), "A requester is required"),
            new ValidationIssue(string.Empty, "The ticket is incomplete"),
            new ValidationIssue(nameof(Ticket.Impact), "Impact helps triage", ValidationSeverity.Warning)));
        using var reference = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Reference)));
        using var requester = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Requester)));
        using var impact = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Impact)));

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        AssertAgree(engine, outcome);

        // Each route of the rule is in play: a display name, a path, and the model-level name.
        Assert.Equal(
            new[] { "Requester", ShippedModelLevelName, "Ticket reference" },
            outcome.VisibleErrorSummary.Order(StringComparer.Ordinal));

        // The gate's arm, which the summary names by its own separate expression.
        var order = new EngineOrder();
        using var gated = BuildOrder(order, new EditContext(order), new EngineOrderValidator(), new FormidableOptions());

        var gatedOutcome = await gated.ValidateForSubmitAsync();

        Assert.False(gatedOutcome.CanProceed);
        AssertAgree(gated, gatedOutcome);
        Assert.Equal([ShippedModelLevelName], gatedOutcome.VisibleErrorSummary);
    }

    [Fact]
    public async Task A_hand_built_VisibleIssue_has_no_name_and_equals_the_engine_s()
    {
        // Mutations: delete VisibleIssue's declared Equals and GetHashCode, and the synthesized
        // equality compares the name too, so the hand-built pair no longer equals the engine's.
        // Or DisplayName defaults to Issue.DisplayName through an initialiser, and the hand-built
        // copy reads "Ticket reference".
        var ticket = new Ticket();
        using var engine = Build(ticket, new FluentValidationModelValidator<Ticket>(new TicketValidator()));
        using var reference = engine.Registry.Register(new FieldIdentifier(ticket, nameof(Ticket.Reference)));
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        var fromEngine = Assert.Single(engine.GetVisibleIssues());

        var handBuilt = new VisibleIssue(fromEngine.Field, fromEngine.Issue);

        Assert.Null(handBuilt.DisplayName);

        // The name is derived from the issue and an option, so it stays out of the record's
        // equality: the same field and issue make the same entry whatever name either carries.
        Assert.Equal(fromEngine, handBuilt);
        Assert.Equal(fromEngine.GetHashCode(), handBuilt.GetHashCode());
        Assert.Equal(fromEngine, handBuilt with { DisplayName = "x" });
    }

    [Fact]
    public void Entries_differing_in_State_are_not_equal()
    {
        // A pin: the issue's own record equality decides, so a State that compares unequal
        // makes a different entry. Mutation: VisibleIssue's Equals compares Issue.Message in
        // place of the whole issue, and the two entries compare equal.
        var ticket = new Ticket();
        var field = new FieldIdentifier(ticket, nameof(Ticket.Reference));
        var issue = new ValidationIssue(nameof(Ticket.Reference), "A reference is required", State: new object());

        var first = new VisibleIssue(field, issue);
        var second = new VisibleIssue(field, issue with { State = new object() });

        Assert.NotEqual(first, second);
        Assert.Equal(first, new VisibleIssue(field, issue));
    }

    // Advisory entries are left out because the summary lists errors alone. It is distinct by
    // name, so the comparison is between sets.
    private static void AssertAgree<TModel>(FormidableEngine<TModel> engine, SubmitOutcome outcome)
        where TModel : class
    {
        var entryNames = engine.GetVisibleIssues()
            .Where(v => v.Issue.Severity == ValidationSeverity.Error)
            .Select(v => v.DisplayName ?? "(no name)")
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(outcome.VisibleErrorSummary.Order(StringComparer.Ordinal), entryNames);
    }
}
