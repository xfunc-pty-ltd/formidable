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
}
