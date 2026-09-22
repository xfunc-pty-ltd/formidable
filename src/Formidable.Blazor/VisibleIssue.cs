using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>A currently-visible validation issue paired with the field it resolved to.</summary>
/// <param name="Field">The resolved field (the model-level identifier for form-level issues).</param>
/// <param name="Issue">The issue, any severity.</param>
public sealed record VisibleIssue(FieldIdentifier Field, ValidationIssue Issue);
