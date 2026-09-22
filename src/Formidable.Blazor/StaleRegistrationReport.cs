using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// What <see cref="FormidableOptions.StaleRegistrationDiagnostic"/> receives when a bound
/// component's accessor no longer names the field it registered: the component's type and both
/// ends of the divergence. <see cref="RegisteredField"/> is where the component's registration,
/// element id, aria attributes and messages still point; <see cref="CurrentField"/> is what the
/// accessor resolves now — the same field on a fresh owner in the common case (an unkeyed row
/// list, a nested object replaced in place), or a different field outright.
/// </summary>
/// <remarks>Grows by init-only properties, never by constructor parameters, so existing
/// construction keeps compiling and binding; any added member folds into the record's
/// synthesized equality.</remarks>
/// <param name="ComponentType">The runtime type of the component whose accessor diverged.</param>
/// <param name="RegisteredField">The field the component registered when it bound.</param>
/// <param name="CurrentField">The field the component's accessor names now.</param>
public sealed record StaleRegistrationReport(
    Type ComponentType,
    FieldIdentifier RegisteredField,
    FieldIdentifier CurrentField);

/// <summary>
/// The engine-side seam a component reports a stale registration through. Implemented explicitly
/// by <see cref="FormidableEngine{TModel}"/> — a test double cascaded in place of the shipped
/// engine does not implement it, so a component bound to one reports nowhere — and it exists so
/// the report shares one implementation with the engine's other diagnostics: the same three
/// channels <c>ReportSuppressed</c> writes, in the same order, rather than a component-side copy
/// free to drift from them.
/// </summary>
internal interface IStaleRegistrationReporter
{
    /// <summary>
    /// Reports <paramref name="report"/> through the diagnostic channels: a Trace line, a logged
    /// warning where the engine holds a logger, and
    /// <see cref="FormidableOptions.StaleRegistrationDiagnostic"/> where one is set.
    /// </summary>
    /// <param name="report">The divergence being reported.</param>
    void Report(StaleRegistrationReport report);
}
