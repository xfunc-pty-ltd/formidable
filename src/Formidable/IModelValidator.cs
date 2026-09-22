namespace Formidable;

/// <summary>
/// The seam between Formidable's engines and a validation implementation. The shipped
/// implementation is <see cref="FluentValidationModelValidator{TModel}"/>; the interface exists
/// so the engines never depend on FluentValidation types directly.
/// </summary>
/// <remarks>
/// Implementing this interface is supported surface, and it grows accordingly: a member added
/// after v1 carries a default implementation, so an implementation written against today's
/// members keeps compiling. One that does not override the addition answers it through the
/// members above — a new validation entry forwards to
/// <see cref="ValidateAsync(TModel, ValidationProfile, CancellationToken)"/> — and so
/// validates exactly as it always has. An ask no default body could honestly answer that way
/// is a new capability, and capabilities arrive as separate optional interfaces with their own
/// testers — the pattern <see cref="IRuleInspectingValidator{TModel}"/> and
/// <see cref="IRuleLevelValidator{TModel}"/> set — rather than as members here.
/// </remarks>
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
