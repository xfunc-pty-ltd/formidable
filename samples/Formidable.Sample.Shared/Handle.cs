using FluentValidation;

namespace Formidable.Sample.Shared;

public class Handle
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Shared by both handle validators, plain and memoized, so their taken-name checks agree
    // without either one holding its own copy of the lists.
    internal static readonly string[] Taken = ["admin", "root", "formidable"];
    internal static readonly string[] TakenDisplayNames = ["Administrator", "Root User", "Formidable"];
}

public class HandleValidator : DraftSubmitValidator<Handle>
{
    // Mutable so the sample page can slow the simulated call down and make cancellation
    // visible; a real validator would inject a clock/service rather than hold mutable state.
    // Shared with MemoizedHandleValidator, which reads the same property rather than holding
    // its own copy.
    public static int SimulatedDelayMs { get; set; } = 600;

    protected override void ConfigureDraftRules()
    {
        // Async uniqueness sits in the always-on (Draft) bucket so a lenient draft save answers
        // it too; what runs it on each committed change is the live channel, which evaluates
        // whatever would block a submit. The delay stands in for a server call and honours
        // cancellation: a newer keystroke cancels the older check before it can answer.
        RuleFor(h => h.Username)
            .MustAsync(async (username, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !Handle.Taken.Contains(username, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That username is taken")
            .When(h => !string.IsNullOrEmpty(h.Username));

        // A second, independent async field — demonstrates that the pending flag is scoped to
        // the field being edited, not the whole form.
        RuleFor(h => h.DisplayName)
            .MustAsync(async (displayName, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !Handle.TakenDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That display name is taken")
            .When(h => !string.IsNullOrEmpty(h.DisplayName));
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(h => h.Username).NotEmpty().WithMessage("Username is required");
    }
}
