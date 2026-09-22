using Bunit;
using Bunit.Rendering;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

// Pins the factory half of the engine's clock seam: FormidableEngineFactory.Create resolves
// TimeProvider from the container, so a host that registers one (FakeTimeProvider here) drives
// the engine's debounce timers by moving the registered clock rather than by waiting out wall
// time. The refresh window is the timer under test, and LiveProfile is narrowed to the draft
// bucket so the fix-up edit's immediate live pass cannot rebuild the submit projection itself
// (it does when LiveProfile resolves to the submit profile) — clearing the stale submit error
// is then the refresh's alone, and the refresh moves only when the registered clock does.
// A host that registers nothing keeps the constructor's own TimeProvider.System fallback.
public class FormidableTimeProviderResolutionTests : BunitContext
{
    private static readonly TimeSpan LongRefreshDebounce = TimeSpan.FromSeconds(5);

    public FormidableTimeProviderResolutionTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Fact]
    public async Task A_container_registered_TimeProvider_drives_the_refresh_debounce()
    {
        var clock = new FakeTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        var order = new EngineOrder { Customer = new EngineCustomer() }; // only Description fails
        var cut = RenderForm(order, new FormidableOptions
        {
            // Wall-clock long on purpose: a refresh that lands inside this test's waits can
            // only have been driven by the registered clock, never by real time passing.
            RefreshDebounce = LongRefreshDebounce,
            LiveProfile = ValidationProfile.Draft,
            DisclosureOverride = _ => true, // no rendered fields, so visibility is overridden
        });
        var engine = cut.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(engine.GetIssues(description)));

        order.Description = "fixed";
        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(description));

        // The edit's own narrowed live pass has come and gone; the stale submit error must
        // survive it, because the refresh the same edit armed waits on a window only the
        // registered clock can close.
        cut.WaitForAssertion(() => Assert.False(engine.IsValidating));
        Assert.NotEmpty(engine.GetIssues(description));

        // The discriminating step: were the factory to stop resolving the provider, this
        // window would ride the wall clock — the advance below would move nothing, and the
        // final assertion would time out long before five real seconds elapse.
        clock.Advance(LongRefreshDebounce + TimeSpan.FromMilliseconds(1));

        cut.WaitForAssertion(() => Assert.Empty(engine.GetIssues(description)));
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order, FormidableOptions options)
    {
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), options);
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }
}
