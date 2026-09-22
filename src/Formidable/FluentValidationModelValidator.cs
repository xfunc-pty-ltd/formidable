using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Adapts a FluentValidation <see cref="IValidator{T}"/> to <see cref="IModelValidator{TModel}"/>.</summary>
public sealed class FluentValidationModelValidator<TModel> : IModelValidator<TModel>
{
    private readonly IValidator<TModel> _validator;

    /// <summary>Wraps the given FluentValidation validator.</summary>
    public FluentValidationModelValidator(IValidator<TModel> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ToReport(await _validator.ValidateAsync(model, profile, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public ValidationReport Validate(TModel model, ValidationProfile profile) =>
        ToReport(_validator.Validate(model, profile));

    private static ValidationReport ToReport(ValidationResult result)
    {
        if (result.Errors.Count == 0)
        {
            return ValidationReport.Empty;
        }

        var issues = result.Errors
            .Select(failure => new ValidationIssue(
                failure.PropertyName,
                failure.ErrorMessage,
                MapSeverity(failure.Severity),
                string.IsNullOrEmpty(failure.ErrorCode) ? null : failure.ErrorCode,
                GetDisplayName(failure)))
            .ToList();

        return new ValidationReport(issues);
    }

    private static ValidationSeverity MapSeverity(Severity severity) => severity switch
    {
        Severity.Warning => ValidationSeverity.Warning,
        Severity.Info => ValidationSeverity.Info,
        _ => ValidationSeverity.Error
    };

    private static string? GetDisplayName(ValidationFailure failure) =>
        failure.FormattedMessagePlaceholderValues is { } values
        && values.TryGetValue("PropertyName", out var name)
            ? name?.ToString()
            : null;
}
