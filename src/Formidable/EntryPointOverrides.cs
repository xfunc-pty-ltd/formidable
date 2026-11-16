using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Finds a validator class's override of FluentValidation's per-call entry points, <c>Validate</c> and <c>ValidateAsync</c> taking a <see cref="ValidationContext{T}"/>, which every validation of the validator passes through.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
// Each check binds the entry point as a C# method group on the validator itself. Making a
// delegate from a method group on a virtual method resolves the override against the
// instance's class, as a call does. So the delegate's Method is the method a call reaches, an
// override that narrows the return type included. Nothing is looked up by name, so trimming has
// nothing to keep. A delegate built from the base declaration's MethodInfo is not used: a
// runtime may report that declaration as its Method even where a call reaches the override.
internal static class EntryPointOverrides<TModel>
{
    /// <summary>Whether the class of <paramref name="validator"/> overrides <c>ValidateAsync(ValidationContext&lt;T&gt;, CancellationToken)</c>, declared there or inherited from a base between it and <see cref="AbstractValidator{T}"/>.</summary>
    /// <param name="validator">The validator to read.</param>
    /// <returns><see langword="true"/> when a call reaches a method other than <see cref="AbstractValidator{T}"/>'s own.</returns>
    internal static bool OverridesValidateAsync(AbstractValidator<TModel> validator)
    {
        Func<ValidationContext<TModel>, CancellationToken, Task<ValidationResult>> entryPoint = validator.ValidateAsync;
        return entryPoint.Method.DeclaringType != typeof(AbstractValidator<TModel>);
    }

    /// <summary>Whether the class of <paramref name="validator"/> overrides <c>Validate(ValidationContext&lt;T&gt;)</c>, declared there or inherited from a base between it and <see cref="AbstractValidator{T}"/>.</summary>
    /// <param name="validator">The validator to read.</param>
    /// <returns><see langword="true"/> when a call reaches a method other than <see cref="AbstractValidator{T}"/>'s own.</returns>
    internal static bool OverridesValidate(AbstractValidator<TModel> validator)
    {
        Func<ValidationContext<TModel>, ValidationResult> entryPoint = validator.Validate;
        return entryPoint.Method.DeclaringType != typeof(AbstractValidator<TModel>);
    }
}
