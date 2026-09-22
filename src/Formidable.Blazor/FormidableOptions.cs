namespace Formidable.Blazor;

/// <summary>Engine configuration. Profiles are assigned meaning here — at the point of use.</summary>
public sealed class FormidableOptions
{
    /// <summary>Profile run on field changes. Defaults to <see cref="ValidationProfile.Draft"/>.</summary>
    public ValidationProfile LiveProfile { get; set; } = ValidationProfile.Draft;

    /// <summary>Profile run by the submit pipeline. Defaults to <see cref="ValidationProfile.Submit"/>.</summary>
    public ValidationProfile SubmitProfile { get; set; } = ValidationProfile.Submit;

    /// <summary>Debounce for the post-submit refresh. Defaults to 300 ms.</summary>
    public TimeSpan RefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Optional disclosure override. Return true to force an issue visible, false to force it
    /// suppressed, or null to defer to the field registry. Model-level issues (empty path) are
    /// always visible unless this returns false.
    /// </summary>
    public Func<ValidationIssue, bool?>? DisclosureOverride { get; set; }

    /// <summary>
    /// Invoked once per error issue suppressed at submit because no rendered field
    /// registration matched and no disclosure override applied — usually a missing
    /// wrapper or <c>FormidableFieldAnchor</c>. A Trace-output warning is emitted regardless, and
    /// so is a logged warning when the host resolved an <c>ILoggerFactory</c> — WASM's default
    /// logging provider is the browser console, so that channel needs no wiring here to be seen.
    /// </summary>
    public Action<ValidationIssue>? SuppressedIssueDiagnostic { get; set; }

    /// <summary>Class names field components and native InputBase components apply based on field state.</summary>
    public FormidableCssClasses CssClasses { get; set; } = new();
}
