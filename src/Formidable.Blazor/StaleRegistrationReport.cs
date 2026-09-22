using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>What <see cref="FormidableOptions.StaleRegistrationDiagnostic"/> receives: the component whose accessor moved, the field it registered, and the field it currently names.</summary>
/// <param name="ComponentType">The runtime type of the component whose accessor diverged.</param>
/// <param name="RegisteredField">The field the component registered when it bound, where its element id, aria attributes and messages still point.</param>
/// <param name="CurrentField">The field the accessor currently resolves: the same field on a fresh owner, or a different field.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record StaleRegistrationReport(
    Type ComponentType,
    FieldIdentifier RegisteredField,
    FieldIdentifier CurrentField);

/// <summary>The seam a component reports a stale registration through; the shipped engine implements it and a test double need not, in which case the component reports nowhere.</summary>
// Implemented explicitly by the engine so the report shares one implementation with its other
// diagnostics: the same three channels ReportSuppressed writes, in the same order, rather than a
// component-side copy free to drift from them.
internal interface IStaleRegistrationReporter
{
    /// <summary>Reports <paramref name="report"/>: a Trace line, a logged warning where the engine holds a logger, and <see cref="FormidableOptions.StaleRegistrationDiagnostic"/> where one is set.</summary>
    /// <param name="report">The divergence being reported.</param>
    void Report(StaleRegistrationReport report);
}
