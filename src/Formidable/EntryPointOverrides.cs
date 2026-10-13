using System.Reflection;
using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Finds a validator class's override of FluentValidation's per-call entry points, <c>Validate</c> and <c>ValidateAsync</c> taking a <see cref="ValidationContext{T}"/>, which every validation of the validator passes through.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
// Found as FluentValidationModelValidator finds a PreValidate override: each base declaration
// comes from a delegate this class binds to itself, never from a lookup by name, so trimming has
// nothing to keep, and a virtual method bound to an instance dispatches through that instance's
// class, so the bound delegate's Method is the most derived override.
internal sealed class EntryPointOverrides<TModel> : AbstractValidator<TModel>
{
    private static readonly MethodInfo ValidateDeclaration = new EntryPointOverrides<TModel>().BindValidate().Method;

    private static readonly MethodInfo ValidateAsyncDeclaration = new EntryPointOverrides<TModel>().BindValidateAsync().Method;

    /// <summary>Whether the class of <paramref name="validator"/> overrides <c>ValidateAsync(ValidationContext&lt;T&gt;, CancellationToken)</c>, declared there or inherited from a base between it and <see cref="AbstractValidator{T}"/>.</summary>
    /// <param name="validator">The validator to read.</param>
    /// <returns><see langword="true"/> when a call reaches a method other than <see cref="AbstractValidator{T}"/>'s own.</returns>
    internal static bool OverridesValidateAsync(AbstractValidator<TModel> validator) =>
        Delegate.CreateDelegate(typeof(Func<ValidationContext<TModel>, CancellationToken, Task<ValidationResult>>), validator, ValidateAsyncDeclaration)
            .Method.DeclaringType != typeof(AbstractValidator<TModel>);

    /// <summary>Whether the class of <paramref name="validator"/> overrides <c>Validate(ValidationContext&lt;T&gt;)</c>, declared there or inherited from a base between it and <see cref="AbstractValidator{T}"/>.</summary>
    /// <param name="validator">The validator to read.</param>
    /// <returns><see langword="true"/> when a call reaches a method other than <see cref="AbstractValidator{T}"/>'s own.</returns>
    internal static bool OverridesValidate(AbstractValidator<TModel> validator) =>
        Delegate.CreateDelegate(typeof(Func<ValidationContext<TModel>, ValidationResult>), validator, ValidateDeclaration)
            .Method.DeclaringType != typeof(AbstractValidator<TModel>);

    private Func<ValidationContext<TModel>, ValidationResult> BindValidate() => Validate;

    private Func<ValidationContext<TModel>, CancellationToken, Task<ValidationResult>> BindValidateAsync() => ValidateAsync;
}
