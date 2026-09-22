namespace Formidable;

/// <summary>
/// The seam between Formidable's engines and a validation implementation. The shipped
/// implementation is <see cref="FluentValidationModelValidator{TModel}"/>; the interface exists
/// so the engines never depend on FluentValidation types directly.
/// </summary>
public interface IModelValidator<in TModel>
{
    /// <summary>Validates the model under the given profile.</summary>
    Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous validation. Only safe when the underlying validator contains no async
    /// rules (FluentValidation throws otherwise). Prefer
    /// <see cref="ValidateAsync(TModel, ValidationProfile, CancellationToken)"/>.
    /// </summary>
    ValidationReport Validate(TModel model, ValidationProfile profile);
}
