namespace Formidable.Shared;

/// <summary>The one message the Blazor and ASP.NET Core packages raise when Formidable's <c>IModelValidator&lt;T&gt;</c> adapter is registered and the FluentValidation validator it wraps is not.</summary>
// The likeliest first-run wiring mistake: resolving the adapter then fails during activation
// rather than returning null. The container's own text names the adapter's internals instead of
// the missing registration, and under WebAssembly's default trimming it degrades further to a
// bare resource key; Formidable's own string is the only one trimming cannot take away. Compiled
// into both Formidable.Blazor and Formidable.AspNetCore from this one shared source file, so the
// two hosts report one state one way.
internal static class MissingFluentValidatorMessage
{
    /// <summary>Names the missing validator registration and the two ways to make it.</summary>
    /// <param name="modelType">The model type whose validator is missing.</param>
    /// <returns>The message, naming the model type as a developer wrote it.</returns>
    internal static string For(Type modelType)
    {
        var name = FriendlyTypeName.Of(modelType);
        return $"No FluentValidation validator for '{name}' is registered, so Formidable's " +
            $"IModelValidator<{name}> adapter cannot be constructed. Register one with " +
            $"services.AddScoped<IValidator<{name}>, {name}Validator>(), or register a whole " +
            "assembly's validators at once with services.AddValidatorsFromAssembly().";
    }
}
