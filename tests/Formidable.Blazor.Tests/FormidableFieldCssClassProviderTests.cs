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
/// internally.
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

    [Fact]
    public void Modified_error_free_field_gets_the_valid_class()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = "short"; // draft rule (MaximumLength(10)) passes
        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.IsModified(field));
        Assert.Empty(engine.EditContext.GetValidationMessages(field));
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

    // Deliberate behavior change: the provider's Valid predicate reads touched-or-modified through
    // the same rule FormidableCss.Compute applies to a Formidable input, so engine-level touch
    // alone earns the Valid class on the native path too -- one decision, one owner, both seams.
    [Fact]
    public void Engine_level_touched_alone_earns_the_valid_class_matching_the_kit_seam()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.MarkTouched(field);

        Assert.True(engine.GetFieldState(field).IsTouched);
        Assert.False(engine.EditContext.IsModified(field));

        var providerClass = engine.EditContext.FieldCssClass(field);
        var kitClass = FormidableCss.Compute(engine.GetFieldState(field), engine.Options.CssClasses);

        Assert.Equal("formidable-valid", providerClass);
        Assert.Equal(kitClass, providerClass);
    }

    [Fact]
    public async Task Field_validating_gets_the_pending_class_alongside_whatever_else_currently_applies()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = CreateEngine(order, validator, new FormidableOptions { LiveProfile = ValidationProfile.Submit });
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.EditContext.NotifyFieldChanged(field); // starts the live pass; GatedValidator's async rule blocks on Gate

        Assert.True(engine.GetFieldState(field).IsValidating);
        Assert.Equal("formidable-valid formidable-pending", engine.EditContext.FieldCssClass(field));

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
        var state = new FieldState(IsTouched: true, IsModified: false, IsValidating: true, HasErrors: false, HasWarnings: false);
        var engine = new FieldStateStubEngine(editContext, state);
        var provider = new FormidableFieldCssClassProvider(new FormidableCssClasses(), engine);

        Assert.False(editContext.IsModified(field));

        Assert.Equal("formidable-valid formidable-pending", provider.GetFieldCssClass(editContext, field));
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
}
