using Microsoft.AspNetCore.Http;

namespace Formidable.AspNetCore;

/// <summary>Extension methods that read the validation report Formidable's server adapters recorded on the current request.</summary>
public static class FormidableHttpContextExtensions
{
    // The HttpContext.Items key both server adapters stash the computed report under. Internal
    // on purpose: the accessor below is the contract, and keeping the key out of the public
    // surface leaves the storage channel free to change.
    internal const string ValidationReportKey = "Formidable.AspNetCore.ValidationReport";

    /// <summary>The validation report an adapter computed for the current request, or <see langword="null"/> when none validated a model on it.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>The report, recorded before the adapter chose between the 400 and the handler and so readable on either path; <see langword="null"/> when no adapter recorded one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static ValidationReport? GetFormidableValidationReport(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ValidationReportKey, out var value)
            ? value as ValidationReport
            : null;
    }

    /// <summary>Records <paramref name="report"/> as the current request's computed report, for <see cref="GetFormidableValidationReport"/> to read.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="report">The report the adapter computed.</param>
    internal static void SetFormidableValidationReport(this HttpContext context, ValidationReport report)
    {
        context.Items[ValidationReportKey] = report;
    }
}
