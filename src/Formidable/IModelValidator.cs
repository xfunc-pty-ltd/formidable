namespace Formidable;

/// <summary>Validates a model under a <see cref="ValidationProfile"/>; the seam the Blazor engine and the server adapters validate through.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// <see cref="FluentValidationModelValidator{TModel}"/> adapts a FluentValidation validator;
/// <see cref="DelegatingModelValidator{TModel}"/> is the base for a validator that wraps another.
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling. A new
/// capability arrives as a separate optional interface with its own tester, as
/// <see cref="IRuleInspectingValidator{TModel}"/> and <see cref="IRuleLevelValidator{TModel}"/> do.
/// </remarks>
// The interface exists so the engines never depend on FluentValidation types directly. A member
// added here answers through the members above (a new validation entry forwards to
// ValidateAsync), so an implementation that does not override it validates exactly as before;
// an ask no default body could honestly answer that way is a new capability, and it arrives as a
// separate optional interface with its own tester rather than as a member here.
public interface IModelValidator<in TModel>
{
    /// <summary>Validates <paramref name="model"/> under <paramref name="profile"/>.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The report; <see cref="ValidationReport.IsValid"/> is <see langword="true"/> when no error was found.</returns>
    Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Validates <paramref name="model"/> under <paramref name="profile"/> synchronously; prefer <see cref="ValidateAsync(TModel, ValidationProfile, CancellationToken)"/>.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <returns>The report.</returns>
    /// <exception cref="FluentValidation.AsyncValidatorInvokedSynchronouslyException">Thrown by <see cref="FluentValidationModelValidator{TModel}"/> when a rule <paramref name="profile"/> selects reaches an async validator or an async condition; an async rule the profile leaves out, or whose condition is false, throws nothing.</exception>
    ValidationReport Validate(TModel model, ValidationProfile profile);
}
