using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Formidable.Blazor.Tests;

// Adaptation: bunit 2.9.0's matcher-based SetupVoid overload returns a handler that must be
// explicitly completed via SetVoidResult() (identifier+args
// overloads auto-complete; the InvocationMatcher overload used here does not). Also, resolving
// FormidableFocusService (IAsyncDisposable-only, per the binding contract) directly from
// BunitContext.Services means the .NET DI container captures it for disposal; BunitContext's
// xUnit teardown calls the synchronous IDisposable.Dispose(), which throws for an object that
// only implements IAsyncDisposable. Each test explicitly awaits Services.DisposeAsync() so the
// container is already disposed (idempotent) by the time xUnit's synchronous teardown runs.
public class FocusServiceTests : BunitContext
{
    [Fact]
    public async Task Focus_invokes_the_module_with_the_field_id()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        var invocation = module.Setup<bool>("focusField", _ => true).SetResult(true);
        var order = new EngineOrder();
        var service = Services.GetRequiredService<IFormidableFocusService>();

        var found = await service.FocusAsync(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        Assert.True(found);
        invocation.VerifyInvoke("focusField");
        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // Pins the shared-import-task shape of the fix: two FocusAsync calls started before either is
    // awaited must still result in exactly one "import" call and both "focusField" calls succeeding.
    //
    // NOTE on what this test can and cannot prove: bUnit 2.9.0 has no supported way to hold an
    // IJSRuntime.InvokeAsync<IJSObjectReference> ("import", ...) call pending.
    // `Setup<IJSObjectReference>` explicitly throws
    // ("Use one of the SetupModule() methods instead") and SetupModule's own handler
    // (JSObjectReferenceInvocationHandler) calls SetResult in its constructor - confirmed by reading
    // bUnit's source - so the import always resolves synchronously the moment it is set up. Because
    // of that, `await`ing an already-completed task never yields, so under this mock BOTH the fixed
    // implementation and the pre-fix buggy one (`_module ??= await ...`) run each FocusAsync call to
    // completion before the next one starts - there is no genuine interleaving to observe here, and
    // this test alone cannot distinguish the two. It still guards a real regression: an
    // implementation that dropped the null-coalescing reuse entirely (e.g. always issuing a fresh
    // "import" per call) would fail the import-count assertion below. The concurrency property
    // itself - that the cached task is assigned synchronously, before any await, so a second caller
    // can never observe a not-yet-cached null - is a structural property of the source (verified by
    // inspection: `_moduleTask ??= ...AsTask();` contains no await before the assignment, unlike the
    // pre-fix `_module ??= await ...`), not something this bUnit-mocked test can independently
    // reproduce under real concurrency.
    [Fact]
    public async Task Concurrent_focus_calls_result_in_a_single_module_import()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        var invocation = module.Setup<bool>("focusField", _ => true).SetResult(true);
        var order = new EngineOrder();
        var service = Services.GetRequiredService<IFormidableFocusService>();

        var first = service.FocusAsync(new FieldIdentifier(order, nameof(EngineOrder.Description))).AsTask();
        var second = service.FocusAsync(new FieldIdentifier(order, nameof(EngineOrder.Customer))).AsTask();
        await Task.WhenAll(first, second);

        Assert.Single(JSInterop.Invocations["import"]);
        invocation.VerifyInvoke("focusField", 2);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task AddFormidableBlazor_registers_core_and_focus_services()
    {
        Services.AddFormidableBlazor();

        Assert.NotNull(Services.BuildServiceProvider().GetService<Formidable.Introspection.IModelIntrospector>());
        // Focus service resolves from the bUnit-scoped provider (needs IJSRuntime):
        Assert.NotNull(Services.GetRequiredService<IFormidableFocusService>());

        await Services.DisposeAsync();
    }

    // A faulted import must not be cached: caching it would permanently disable focus for the rest
    // of the circuit after a single transient JS failure. This test uses a hand-rolled IJSRuntime
    // rather than bUnit's JSInterop because bUnit has no supported way to fail a module import -
    // SetupModule's handler resolves the import in its constructor, and Setup<IJSObjectReference>
    // explicitly refuses module imports (see the class remarks above).
    [Fact]
    public async Task A_faulted_module_import_is_retried_by_the_next_focus_call()
    {
        var jsRuntime = new FlakyImportJSRuntime(failures: 1);
        await using var provider = new ServiceCollection()
            .AddFormidableBlazor()
            .AddSingleton<IJSRuntime>(jsRuntime)
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IFormidableFocusService>();
        var field = new FieldIdentifier(new EngineOrder(), nameof(EngineOrder.Description));

        await Assert.ThrowsAsync<JSException>(async () => await service.FocusAsync(field));
        await service.FocusAsync(field);

        Assert.Equal(2, jsRuntime.ImportCount);
        Assert.Equal(1, jsRuntime.Module.FocusCalls);
    }

    /// <summary>Test-only runtime whose first <paramref name="failures"/> "import" calls fault, and whose later ones return <see cref="Module"/>.</summary>
    private sealed class FlakyImportJSRuntime(int failures) : IJSRuntime
    {
        public int ImportCount { get; private set; }

        public FakeModule Module { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportCount++;
            return ImportCount <= failures
                ? ValueTask.FromException<TValue>(new JSException("could not load the module"))
                : ValueTask.FromResult((TValue)(object)Module);
        }
    }

    // Pins the miss path specifically (module returns false, e.g. a virtualized row outside the
    // render window). The hit path is pinned separately, on the existing SetResult(true) setup in
    // Focus_invokes_the_module_with_the_field_id above — together they rule out an implementation
    // that discards the module's result and always returns default(bool).
    [Fact]
    public async Task Focus_returns_the_module_result()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        var order = new EngineOrder();
        var service = Services.GetRequiredService<IFormidableFocusService>();

        var found = await service.FocusAsync(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        Assert.False(found);

        await Services.DisposeAsync();
    }

    /// <summary>Test-only module reference counting the calls the focus service makes through it.</summary>
    private sealed class FakeModule : IJSObjectReference
    {
        public int FocusCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("focusField", identifier);
            FocusCalls++;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
