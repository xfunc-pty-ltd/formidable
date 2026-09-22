namespace Formidable.Shared;

/// <summary>
/// The one wording for the likeliest first-run wiring mistake: Formidable's open-generic
/// <c>IModelValidator&lt;T&gt;</c> adapter is registered, the FluentValidation validator it wraps
/// is not, so resolving the adapter fails during activation rather than returning null. The
/// container's own text names the adapter's internals instead of the missing registration, and
/// under WebAssembly's default trimming it degrades further to a bare resource key — Formidable's
/// own string is the only one trimming cannot take away. Compiled into both
/// <c>Formidable.Blazor</c> and <c>Formidable.AspNetCore</c> from this one shared source file, so
/// the two hosts report one state one way.
/// </summary>
internal static class MissingFluentValidatorMessage
{
    /// <summary>Names the missing validator registration and the two ways to make it.</summary>
    internal static string For(Type modelType)
    {
        var name = FriendlyTypeName.Of(modelType);
        return $"No FluentValidation validator for '{name}' is registered, so Formidable's " +
            $"IModelValidator<{name}> adapter cannot be constructed. Register one with " +
            $"services.AddScoped<IValidator<{name}>, {name}Validator>(), or register a whole " +
            "assembly's validators at once with services.AddValidatorsFromAssembly().";
    }
}
