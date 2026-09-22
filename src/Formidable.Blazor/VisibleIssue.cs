using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>An issue currently showing, paired with the field it resolved to.</summary>
/// <param name="Field">The resolved field; the model-level identifier for a form-level issue.</param>
/// <param name="Issue">The issue, of any severity.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record VisibleIssue(FieldIdentifier Field, ValidationIssue Issue);
