using Microsoft.AspNetCore.Http;

namespace Formidable.AspNetCore;

/// <summary>Reads what Formidable's server adapters recorded on the current request.</summary>
public static class FormidableHttpContextExtensions
{
    // The HttpContext.Items key both server adapters stash the computed report under. Internal
    // on purpose: the accessor below is the contract, and keeping the key out of the public
    // surface leaves the storage channel free to change.
    internal const string ValidationReportKey = "Formidable.AspNetCore.ValidationReport";

    /// <summary>
    /// The <see cref="ValidationReport"/> the current request's Formidable validation computed,
    /// or <see langword="null"/> when no computed report exists. Both adapters stash the
    /// report the moment validation completes, before the 400/pass-through decision, so it is
    /// readable for the rest of the request on the success path (a handler returning a
    /// "saved, but note…" response beside its 200) and on the 400 path (middleware reading the
    /// rejection's full severity detail without parsing the response body) alike. For
    /// <see cref="ValidateAttribute"/> the report is the aggregate across every validated
    /// argument of the action.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the request holds no computed report: no adapter is wired
    /// into the request's pipeline, the endpoint filter met a null-bound model and had nothing
    /// to validate, <see cref="ValidateAttribute"/> found no argument to validate, or the
    /// adapter threw before a report existed (a failing normalize, an unresolvable validator, a
    /// rule's own exception) — the propagating exception, not the accessor, is that request's
    /// outcome. A handler sitting behind <c>Validate&lt;TModel&gt;()</c> observes
    /// <see langword="null"/> in exactly one case: its own declared parameter bound null and the
    /// platform allowed it through, so no model existed to validate. Whenever a model reached
    /// the handler, so did its report.
    /// </remarks>
    public static ValidationReport? GetFormidableValidationReport(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ValidationReportKey, out var value)
            ? value as ValidationReport
            : null;
    }

    /// <summary>
    /// Stashes <paramref name="report"/> as the current request's computed report, for
    /// <see cref="GetFormidableValidationReport"/> to read back. Internal: writing the report is
    /// each server adapter's own job, never a consumer's.
    /// </summary>
    internal static void SetFormidableValidationReport(this HttpContext context, ValidationReport report)
    {
        context.Items[ValidationReportKey] = report;
    }
}
