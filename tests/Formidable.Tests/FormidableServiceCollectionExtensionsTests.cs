using FluentValidation;
using Formidable.Introspection;
using Formidable.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Tests;

public class FormidableServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFormidable_resolves_introspector_and_adapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidator<TestOrder>, TestOrderValidator>();

        services.AddFormidable();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ReflectionModelIntrospector>(provider.GetRequiredService<IModelIntrospector>());
        var modelValidator = provider.GetRequiredService<IModelValidator<TestOrder>>();
        Assert.False(modelValidator.Validate(new TestOrder(), ValidationProfile.Submit).IsValid);
    }

    [Fact]
    public void AddFormidable_does_not_override_existing_registrations()
    {
        var services = new ServiceCollection();
        var custom = new ReflectionModelIntrospector();
        services.AddSingleton<IModelIntrospector>(custom);

        services.AddFormidable();
        using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IModelIntrospector>());
    }

    [Fact]
    public void AddFormidable_supports_scoped_validators_under_scope_validation()
    {
        var services = new ServiceCollection();
        services.AddScoped<IValidator<TestOrder>, TestOrderValidator>();
        services.AddFormidable();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var modelValidator = scope.ServiceProvider.GetRequiredService<IModelValidator<TestOrder>>();
        Assert.False(modelValidator.Validate(new TestOrder(), ValidationProfile.Submit).IsValid);
    }

    /// <summary>
    /// A consumer's own adapter for every model, registered as the same OPEN generic the shipped
    /// one uses — the shape TryAdd is about, since TryAdd compares service types and an open
    /// generic is only ever blocked by another open generic.
    /// </summary>
    private sealed class ConsumerAdapter<TModel> : IModelValidator<TModel>
    {
        public Task<ValidationReport> ValidateAsync(
            TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(TModel model, ValidationProfile profile) => new([]);
    }

    /// <summary>A consumer's own adapter for one model.</summary>
    private sealed class ConsumerValidator : IModelValidator<TestOrder>
    {
        public Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(TestOrder model, ValidationProfile profile) => new([]);
    }

    [Fact]
    public void A_consumer_registered_validator_wins_over_the_shipped_adapter()
    {
        // Two ways a consumer's own IModelValidator can be what resolves, and they win for
        // different reasons — worth pinning together, because the seam is only as swappable as
        // the weaker of them. Whole-container: TryAdd sees the consumer's open generic and adds
        // nothing, so the shipped adapter is not registered at all. Per model: a closed generic
        // beats an open one in the container's own resolution, whichever went in first, so TryAdd
        // is not what decides that half.
        //
        // The consequence is the same either way and is why DelegatingModelValidator<TModel>
        // exists: what resolves is the whole validator, capabilities included, and a consumer's
        // own answers CanInspectRules and CanValidateByRule false.
        var wholeContainer = new ServiceCollection();
        wholeContainer.AddSingleton<IValidator<TestOrder>, TestOrderValidator>();
        wholeContainer.AddTransient(typeof(IModelValidator<>), typeof(ConsumerAdapter<>));
        wholeContainer.AddFormidable();

        using var wholeContainerProvider = wholeContainer.BuildServiceProvider();
        Assert.IsType<ConsumerAdapter<TestOrder>>(
            wholeContainerProvider.GetRequiredService<IModelValidator<TestOrder>>());

        var perModel = new ServiceCollection();
        var custom = new ConsumerValidator();
        perModel.AddSingleton<IValidator<TestOrder>, TestOrderValidator>();
        perModel.AddSingleton<IModelValidator<TestOrder>>(custom);
        perModel.AddFormidable();

        using var perModelProvider = perModel.BuildServiceProvider();
        Assert.Same(custom, perModelProvider.GetRequiredService<IModelValidator<TestOrder>>());

        // Neither choice removes the FluentValidation validator from the container: a consumer
        // who wraps rather than replaces still has it to hand to their wrapper.
        Assert.IsType<TestOrderValidator>(perModelProvider.GetRequiredService<IValidator<TestOrder>>());
    }
}
