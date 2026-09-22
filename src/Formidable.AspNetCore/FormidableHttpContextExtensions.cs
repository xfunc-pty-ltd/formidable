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
    /// into the request's pipeline, the endpoint filter answered a null-bound model with its
    /// 400 before any validator could run, <see cref="ValidateAttribute"/> found no argument to
    /// validate, or the adapter threw before a report existed (a failing normalize, an
    /// unresolvable validator, a rule's own exception) — the propagating exception, not the
    /// accessor, is that request's outcome. A handler sitting behind
    /// <c>Validate&lt;TModel&gt;()</c> never observes <see langword="null"/>: the filter
    /// validated, and stashed, before the handler could run.
    /// </remarks>
    public static ValidationReport? GetFormidableValidationReport(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ValidationReportKey, out var value)
            ? value as ValidationReport
            : null;
    }
}
