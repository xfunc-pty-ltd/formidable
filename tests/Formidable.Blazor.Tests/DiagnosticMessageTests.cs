using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class DiagnosticMessageTests : BunitContext
{
    public DiagnosticMessageTests()
    {
        // Everything FormidableForm resolves EXCEPT the validator seam — the null validator
        // resolution is the behavior under test. (Add further registrations here if
        // FormidableForm.cs resolves more services before its validator lookup.)
        Services.AddSingleton<IModelIntrospector, ReflectionModelIntrospector>();
    }

    [Fact]
    public void Missing_validator_message_uses_friendly_generic_names()
    {
        var model = new List<EngineItem>();

        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableForm<List<EngineItem>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.CloseComponent();
        }));

        Assert.Contains("IModelValidator<List>", exception.Message);
        Assert.DoesNotContain("`", exception.Message);
    }
}
