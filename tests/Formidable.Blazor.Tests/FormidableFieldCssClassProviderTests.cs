using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the class the engine's installed <c>FieldCssClassProvider</c> hands a native
/// <c>InputBase</c> (read via <c>EditContext.FieldCssClass</c>, the same surface
/// <c>InputBase.CssClass</c> itself reads) so it stays observably unchanged, then covers the
/// added Pending class -- and, for the Valid decision specifically, pins it as an alignment with
/// the kit-input path rather than a fixed string, since that decision is deliberately shared
/// (<see cref="FormidableCss.Compute"/>). Going through this surface rather than constructing the
/// provider directly means these assertions hold regardless of how the provider is wired
/// internally. Three further tests cover the advisory tiers the provider synthesizes: a
/// warning-only field earning <c>classes.Warning</c> through the fast path, the same advisory
/// bits surviving the <c>GetFieldState</c> fallback when an engine doesn't implement the reader,
/// and an error still winning outright over a warning on the same field.
/// </summary>
public class FormidableFieldCssClassProviderTests
{
    [Fact]
    public void Untouched_unmodified_error_free_field_gets_no_class()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.Equal(string.Empty, engine.EditContext.FieldCssClass(field));
    }

    // Valid is a promise about submit, so modified-and-error-free alone cannot earn it: the
    // Valid tier additionally requires the submit-selected rules to have answered for the model
    // as it stands, with no error for the field among those answers. Staged on a live channel
    // narrowed to the draft bucket, which is what leaves those rules unanswered — one selecting
    // the submit profile's own rules, as the default does, answers them in the edit's own pass.
    [Fact]
    public void Modified_error_free_field_earns_valid_only_with_fresh_submit_coverage()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(
            order, new EngineOrderValidator(), new FormidableOptions { LiveProfile = ValidationProfile.Draft });
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = "short"; // draft rule (MaximumLength(10)) passes
        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.IsModified(field));
        Assert.Empty(engine.EditContext.GetValidationMessages(field));
        // The live pass answered only the draft selection; the submit-selected rules have no
        // verdict at this stamp, so nothing can vouch the field would pass.
        Assert.Equal(string.Empty, engine.EditContext.FieldCssClass(field));

        // Tracking makes the edit's probe answer the submit selection; the model is
        // submit-valid, so the coverage lands fresh and clean and green follows.
        order.Customer = new EngineCustomer();
        engine.Options.TrackFormValidity = true;
        engine.EditContext.NotifyFieldChanged(field);

        Assert.Equal("formidable-valid", engine.EditContext.FieldCssClass(field));
    }

    [Fact]
    public void Errored_field_gets_the_invalid_class_even_though_it_is_also_modified()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = new string('x', 11); // draft rule (MaximumLength(10)) fails
        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.IsModified(field)); // errors win despite Modified also being true
        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    // The provider reads engine-level touch through the same rule FormidableCss.Compute applies
    // to a Formidable input -- one decision, one owner, both seams. On a model no rule has ever
    // answered for, that shared decision is NO class: touched is necessary for Valid but cannot
    // be sufficient, because the submit-selected rules that would decide the field's fate are
    // unanswered (and this empty model would in fact fail them).
    [Fact]
    public void Engine_level_touched_alone_matches_the_kit_seam_and_earns_nothing_while_coverage_is_stale()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.MarkTouched(field);

        Assert.True(engine.GetFieldState(field).IsTouched);
        Assert.False(engine.EditContext.IsModified(field));

        var providerClass = engine.EditContext.FieldCssClass(field);
        var kitClass = FormidableCss.Compute(engine.GetFieldState(field), engine.Options.CssClasses);

        Assert.Equal(string.Empty, providerClass);
        Assert.Equal(kitClass, providerClass);
    }

    // The provider's fast path (IValidatingFieldReader) answers the same HasWarnings/HasInfos
    // question GetFieldState's severity scan does, so a warning-only field reaches
    // classes.Warning through the native path exactly as it already does through a Formidable
    // input.
    [Fact]
    public void A_warning_only_field_gets_the_warning_class()
    {
        var order = new EngineOrder { Description = "ab-" }; // <=3 chars: the error rule passes; the hyphen trips only the warning
        using var engine = CreateEngine(order, new DraftErrorAndWarningValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.EditContext.NotifyFieldChanged(field); // synchronous draft rules settle within this call

        Assert.False(engine.EditContext.GetValidationMessages(field).Any());
        Assert.Equal("formidable-warning", engine.EditContext.FieldCssClass(field));
    }

    // Constructs the provider directly with an engine double that answers GetFieldState with real
    // advisory bits and does NOT implement the fast path, proving the fallback branch carries
    // fallback.HasWarnings/HasInfos through to the returned class.
    [Fact]
    public void The_fallback_engine_still_answers()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var state = new FieldState(IsTouched: true, IsModified: false, IsValidating: false, HasErrors: false, HasWarnings: true, HasInfos: false);
        var engine = new FieldStateStubEngine(editContext, state);
        var provider = new FormidableFieldCssClassProvider(new FormidableCssClasses(), engine);

        Assert.Equal("formidable-warning", provider.GetFieldCssClass(editContext, field));
    }

    // Same field, same pass: an error rule and a warning rule both fail, so the provider's
    // HasErrors read (straight off the EditContext) and its HasWarnings read (through the reader)
    // land on the same field at once -- Compute's error-wins-ungated ordering must still hold.
    [Fact]
    public void Errors_still_win_through_the_provider()
    {
        var order = new EngineOrder { Description = "abcd-" }; // >3 chars trips the error rule; the hyphen also trips the warning
        using var engine = CreateEngine(order, new DraftErrorAndWarningValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.GetValidationMessages(field).Any());
        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    [Fact]
    public async Task Field_validating_gets_the_pending_class_alongside_whatever_else_currently_applies()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = CreateEngine(order, validator, new FormidableOptions());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.EditContext.NotifyFieldChanged(field); // starts the live pass; GatedValidator's async rule blocks on Gate

        // Mid-pass the freshly-edited model has no current submit answer — the pass computing
        // one is exactly what is in flight — so Pending rides alone rather than on top of a
        // Valid the field has not earned yet.
        Assert.True(engine.GetFieldState(field).IsValidating);
        Assert.Equal("formidable-pending", engine.EditContext.FieldCssClass(field));

        var quiescent = new TaskCompletionSource();
        engine.StateChanged += () =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };

        validator.Gate.SetResult();
        await quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Pending clears once the pass ends; the async rule's failure wins over the earlier Valid.
        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    // FormidableFieldCssClassProvider prefers an internal fast path (IValidatingFieldReader) that
    // FormValidationEngine<TModel> implements for both the touched and the pending reads, and
    // every test above goes through a real engine -- so those tests only ever exercise that fast
    // path. This one constructs the provider directly with an IFormValidationEngine that does NOT
    // implement the fast-path interface (the same shape any third-party engine implementation
    // has), to prove the GetFieldState(...).IsTouched/.IsValidating fallbacks actually run: the
    // EditContext is never modified, so a Valid class can only have come from the touched
    // fallback, not from IsModified (which the provider still reads straight off the EditContext).
    [Fact]
    public void Provider_falls_back_to_GetFieldState_when_the_engine_has_no_fast_path()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var state = new FieldState(IsTouched: true, IsModified: false, IsValidating: true, HasErrors: false, HasWarnings: false, HasInfos: false);
        var engine = new FieldStateStubEngine(editContext, state);
        var provider = new FormidableFieldCssClassProvider(new FormidableCssClasses(), engine);

        Assert.False(editContext.IsModified(field));

        Assert.Equal("formidable-valid formidable-pending", provider.GetFieldCssClass(editContext, field));
    }

    // The fallback branch carries the WouldPassSubmit bit exactly as it carries the advisory
    // bits: an engine whose GetFieldState answers false for it denies the Valid class through
    // the provider too, with every other input identical to the test above.
    [Fact]
    public void The_fallback_engine_denies_valid_when_it_cannot_vouch_for_submit()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: true,
            HasErrors: false, HasWarnings: false, HasInfos: false, WouldPassSubmit: false);
        var engine = new FieldStateStubEngine(editContext, state);
        var provider = new FormidableFieldCssClassProvider(new FormidableCssClasses(), engine);

        Assert.Equal("formidable-pending", provider.GetFieldCssClass(editContext, field));
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(
        EngineOrder order,
        FluentValidation.IValidator<EngineOrder> validator,
        FormidableOptions? options = null) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options ?? new FormidableOptions(),
            new FakeTimeProvider());

    /// <summary>
    /// A minimal <see cref="IFormValidationEngine"/> that answers every field's state with a
    /// fixed <see cref="FieldState"/> and implements nothing beyond the interface — deliberately
    /// not the internal fast-path capability <see cref="FormValidationEngine{TModel}"/> also
    /// implements, so a provider constructed with one must fall back to <see cref="GetFieldState"/>.
    /// </summary>
    private sealed class FieldStateStubEngine(EditContext editContext, FieldState state) : IFormValidationEngine
    {
        public EditContext EditContext { get; } = editContext;

        public FieldRegistry Registry { get; } = new();

        public FormidableOptions Options { get; } = new();

        public bool IsValidating => state.IsValidating;

        public bool HasSubmitted => false;

        public bool IsFormValid => false;

        public event Action? StateChanged
        {
            add { }
            remove { }
        }

        public event Action<Exception>? ValidationFaulted
        {
            add { }
            remove { }
        }

        public FieldState GetFieldState(FieldIdentifier field) => state;

        public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field) => [];

        public IReadOnlyList<VisibleIssue> GetVisibleIssues() => [];

        public void MarkTouched(FieldIdentifier field)
        {
        }

        public Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by this stub's test.");

        public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
        {
        }
    }

    /// <summary>
    /// Draft validator whose one field carries both an error rule and a warning rule that can
    /// fail independently: a description at most 3 characters long only ever trips the hyphen
    /// warning, while one longer than that trips the length error too -- giving a single fixture
    /// that produces a warning-only field for one test and an error-plus-warning field for another.
    /// </summary>
    private sealed class DraftErrorAndWarningValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(x => x.Description).MaximumLength(3).WithMessage("Description is too long");
            RuleFor(x => x.Description)
                .Must(d => !d.Contains('-'))
                .WithSeverity(Severity.Warning)
                .WithMessage("Avoid hyphens");
        }

        protected override void ConfigureSubmitRules()
        {
        }
    }
}
