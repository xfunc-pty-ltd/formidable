using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineHardeningTests
{
    [Fact]
    public async Task Edit_during_first_submit_gets_revalidated_after_submit_completes()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        var submit = engine.ValidateForSubmitAsync();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // debounce elapses while submit is in flight -> defer + re-arm

        validator.Gate.SetResult();
        await submit;
        Assert.Equal(1, validator.Started);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh now runs
        Assert.Equal(2, validator.Started);
    }

    // Pins the post-submit lifetime of advisories (warnings/info). At submit the engine captures
    // non-error issues for every VISIBLE field, which includes fields carrying no error at all.
    // The debounced refresh must keep those fields' advisories current - an advisory view read
    // through the error-reveal ledger alone would silently drop a warning shown on an error-free
    // field the moment any other field changed.
    [Fact]
    public async Task Submit_warning_on_an_error_free_field_survives_the_debounced_refresh()
    {
        var order = new EngineOrder { Description = "a-b" }; // NotEmpty passes; the hyphen warning fires
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        // Customer is null, so submit records an error elsewhere and HasSubmitted stays true.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // the debounced refresh pass runs
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    // Same lifetime pin as above, but with NO co-occurring error anywhere in the model: the
    // submit report is otherwise valid (report.IsValid == true), so this exercises the
    // valid branch's advisory-ledger re-freeze rather than the invalid branch's union. A valid
    // branch that reset the advisory ledger without re-freezing it to the fresh sites would
    // leave the warning captured at submit but drop it the moment any field changes and the
    // debounced refresh runs, because the advisory view discloses only fields one of the two
    // reveal ledgers watches and the error ledger is empty on an errors-free submit.
    [Fact]
    public async Task Submit_warning_with_no_errors_anywhere_survives_the_debounced_refresh()
    {
        var order = new EngineOrder { Description = "a-b", Customer = new EngineCustomer() }; // NotEmpty and NotNull both pass; only the hyphen warning fires
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        // No error anywhere in the model, so this submit takes the report.IsValid branch.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        // Nothing to fix here (there was never an error) - just trigger some field change so
        // HasSubmitted schedules the debounced refresh.
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // the debounced refresh pass runs
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    // RunRefreshPassAsync's own entry guard, pinned the same way its debounced-live-pass sibling
    // already is: a timer fire dispatches RunRefreshPassAsync through _renderDispatch, and that
    // dispatch can still be QUEUED — not yet run — when Dispose() tears the engine down. Without
    // an entry guard, the queued call reaches BeginPass and cancels a _passCts Dispose() already
    // cancelled and disposed, throwing ObjectDisposedException into a discarded task. LiveDebounce
    // is left unset, so the field change below runs its live pass synchronously and fully - before
    // interceptNext ever flips true - leaving the refresh timer's own dispatch as the only thing
    // this test's dispatch override ever gets a chance to intercept.
    [Fact]
    public async Task Refresh_dispatch_still_queued_when_engine_is_disposed_does_not_throw()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var interceptNext = false;
        Func<Task>? queued = null;
        var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time,
            renderDispatch: work =>
            {
                if (interceptNext)
                {
                    interceptNext = false;
                    queued = work;
                    return Task.CompletedTask;
                }

                return work();
            });

        await engine.ValidateForSubmitAsync(); // report.IsValid -> HasSubmitted, no errors to chase
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        interceptNext = true;
        time.Advance(TimeSpan.FromMilliseconds(301)); // arms + fires the refresh timer only
        Assert.NotNull(queued);

        engine.Dispose();

        await queued!(); // the queued dispatch finally runs against the now-disposed engine
    }

    [Fact]
    public async Task ApplyServerIssues_clears_a_prior_fault()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();
        Assert.NotEmpty(editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)));

        engine.ApplyServerIssues([new ValidationIssue("Description", "server said no")]);

        Assert.DoesNotContain(
            editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)),
            m => m.Contains("could not run to completion"));
        Assert.Contains("server said no",
            editContext.GetValidationMessages(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task Suppressed_issues_reach_the_diagnostic_callback()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { SuppressedIssueDiagnostic = suppressed.Add }, new FakeTimeProvider());
        using var reg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        await engine.ValidateForSubmitAsync();

        Assert.Contains(suppressed, i => i.Path == nameof(EngineOrder.Customer)); // unregistered -> suppressed
        Assert.DoesNotContain(suppressed, i => i.Path == nameof(EngineOrder.Description)); // registered -> visible
    }

    [Fact]
    public async Task Diagnostic_is_not_invoked_when_override_reveals_everything()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        using var engine = new FormidableEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true, SuppressedIssueDiagnostic = suppressed.Add },
            new FakeTimeProvider());

        await engine.ValidateForSubmitAsync();

        Assert.Empty(suppressed);
    }
}
