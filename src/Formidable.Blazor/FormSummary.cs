using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renders a live, severity-grouped summary of every currently-visible validation issue across
/// the form, backed by <see cref="IFormValidationEngine.GetVisibleIssues"/> — the same fault-first,
/// submit-then-live-deduped view <c>FieldMessage</c>/<c>CollectionMessage</c> use per-field, but
/// for the whole form at once. Renders nothing while the form has no visible issues; otherwise an
/// <c>aria-alert</c> region with one list per non-empty severity group (errors, then warnings,
/// then infos), each item a button that moves focus to the offending field via
/// <see cref="IFormidableFocusService"/>. Subscribes to the cascaded engine's
/// <see cref="IFormValidationEngine.StateChanged"/> so the summary stays current through live
/// edits, refreshes, and server-applied issues — not just at submit time.
/// </summary>
public sealed class FormSummary : ComponentBase, IDisposable
{
    private readonly FormContextBinding _binding = new();

    [CascadingParameter]
    private FormidableFormContext? Context { get; set; }

    [Inject]
    private IFormidableFocusService FocusService { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnParametersSet() =>
        _binding.Update(Context, GetType(), stateChanged: OnEngineStateChanged);

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        var visibleIssues = Context.Engine.GetVisibleIssues();
        if (visibleIssues.Count == 0)
        {
            return;
        }

        var groups = visibleIssues
            .GroupBy(v => v.Issue.Severity)
            .OrderBy(g => g.Key);

        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", "alert");

        foreach (var group in groups)
        {
            var severitySuffix = group.Key switch
            {
                ValidationSeverity.Error => "--error",
                ValidationSeverity.Warning => "--warning",
                _ => "--info"
            };

            builder.OpenElement(sequence++, "ul");
            builder.AddAttribute(sequence++, "class", $"formidable-summary__group formidable-summary__group{severitySuffix}");

            foreach (var visibleIssue in group)
            {
                builder.OpenElement(sequence++, "li");
                builder.AddAttribute(sequence++, "class", "formidable-summary__item");

                builder.OpenElement(sequence++, "button");
                builder.AddAttribute(sequence++, "type", "button");
                builder.AddAttribute(sequence++, "class", "formidable-summary__link");
                builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, () => FocusService.FocusAsync(visibleIssue.Field).AsTask()));
                builder.AddContent(sequence++, visibleIssue.Issue.Message);
                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseElement();
        }

        builder.CloseElement();
    }

    private void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose() => _binding.Dispose();
}
