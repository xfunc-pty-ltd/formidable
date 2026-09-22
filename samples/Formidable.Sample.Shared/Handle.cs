using FluentValidation;

namespace Formidable.Sample.Shared;

public class Handle
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class HandleValidator : DraftSubmitValidator<Handle>
{
    private static readonly string[] Taken = ["admin", "root", "formidable"];
    private static readonly string[] TakenDisplayNames = ["Administrator", "Root User", "Formidable"];

    // Mutable so the sample page can slow the simulated call down and make cancellation
    // visible; a real validator would inject a clock/service rather than hold mutable state.
    public static int SimulatedDelayMs { get; set; } = 600;

    // Held as fields so they outlive a single pass — the engine keeps one validator instance for
    // as long as it is registered, but a memo built inside a rule's own lambda is rebuilt on
    // every call and never once hits (AsyncRuleMemo's own remarks say so). What reuses an answer
    // here is a submit that follows a live pass: submit always runs the whole submit profile, so
    // it re-checks a value the live pass already answered, and this is the second call that gets
    // to skip the round trip. That gap is however long the person takes between finishing typing
    // and pressing Submit, so the window is sized to that, not to any of the engine's own
    // scheduling windows. Ten seconds is a judgement call rather than a derived value —
    // AsyncRuleMemo's own docs say the same thing: size a window to the pause it has to survive,
    // paced by the person at the keyboard, not by a timer. This is a plausible upper bound on the
    // pause between finishing typing and pressing Submit without pretending to know exactly how
    // long that pause is.
    private readonly AsyncRuleMemo<string, bool> _usernameMemo = new(TimeSpan.FromSeconds(10));
    private readonly AsyncRuleMemo<string, bool> _displayNameMemo = new(TimeSpan.FromSeconds(10));

    protected override void ConfigureDraftRules()
    {
        // Async uniqueness runs in the live (Draft) profile so it fires as the user types;
        // the delay stands in for a server call and honours cancellation, so a superseded
        // keystroke's check is abandoned. MustAsyncMemoized also lets a repeated value — typing
        // "admin", clearing it, then typing "admin" again — answer from the memo instead of
        // paying for the call twice.
        RuleFor(h => h.Username)
            .MustAsyncMemoized(_usernameMemo, async (username, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !Taken.Contains(username, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That username is taken")
            .When(h => !string.IsNullOrEmpty(h.Username));

        // A second, independent async field — demonstrates that the pending indicator during a
        // live pass is scoped to the field being edited, not the whole form.
        RuleFor(h => h.DisplayName)
            .MustAsyncMemoized(_displayNameMemo, async (displayName, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !TakenDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That display name is taken")
            .When(h => !string.IsNullOrEmpty(h.DisplayName));
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(h => h.Username).NotEmpty().WithMessage("Username is required");
    }
}
