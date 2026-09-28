namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Answers every profile with the same issues, so a test chooses each issue's severity, path and
/// display name exactly. FluentValidation names every failure it reports, so only a validator of
/// one's own (or a server reply) can hand a form an issue with a path and no name.
/// </summary>
/// <typeparam name="TModel">The model the form validates.</typeparam>
public sealed class FixedReportValidator<TModel>(params ValidationIssue[] issues) : IModelValidator<TModel>
{
    public Task<ValidationReport> ValidateAsync(
        TModel model,
        ValidationProfile profile,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Validate(model, profile));

    public ValidationReport Validate(TModel model, ValidationProfile profile) => new(issues);
}
