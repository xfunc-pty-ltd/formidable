using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Non-generic engine view consumed by components and the cascaded form context.</summary>
public interface IFormValidationEngine
{
    /// <summary>The edit context this engine writes messages to.</summary>
    EditContext EditContext { get; }

    /// <summary>The disclosure registry rendered fields register with.</summary>
    FieldRegistry Registry { get; }

    /// <summary>True while a validation pass is in flight.</summary>
    bool IsValidating { get; }

    /// <summary>True once the submit pipeline has run (and validation failed or succeeded).</summary>
    bool HasSubmitted { get; }

    /// <summary>Raised whenever validation or field state changes.</summary>
    event Action? StateChanged;

    /// <summary>Current state for one field.</summary>
    FieldState GetFieldState(FieldIdentifier field);

    /// <summary>Marks a field as touched (called by field components on user interaction).</summary>
    void MarkTouched(FieldIdentifier field);

    /// <summary>Runs the submit pipeline: validate with the submit profile, surface visible issues, record the submit-visible set.</summary>
    Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies server-declared issues (e.g. from a 400 ValidationProblemDetails) as if they were submit results.</summary>
    void ApplyServerIssues(IReadOnlyList<ValidationIssue> issues);
}
