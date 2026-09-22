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
}
