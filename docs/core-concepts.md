# Core concepts

One FluentValidation validator has one rule set, and a form asks it two different questions
at two different moments. Require a name too eagerly and a fresh page complains before the
visitor has typed a single character. Wait for submit before checking anything and a
malformed email sits wrong through the whole form, discovered only when the visitor finally
commits. Split the rules by hand — one subset for typing, one for submitting — and every
validator either duplicates its rules across two classes or grows an if/else keyed on a flag
nobody remembers the meaning of six months later. Formidable answers this with two profiles
built into the same validator, and one rule about who is allowed to speak: shape rules and
presence rules sit side by side, each declared once, and a fresh page stays quiet because a
message waits for the visitor to change the field rather than because a rule was switched off.

## Draft and submit, not one rule set doing two jobs

Two profiles ship as a convention. `ValidationProfile.Draft` runs the validator's default
(unnamed) rules only. `ValidationProfile.Submit` runs those same default rules plus a
`"Submit"` ruleset. The convention behind the split: draft rules ask "is this value
malformed?" — wrong length, wrong shape — and treat an empty value as fine, since a blank
field mid-draft isn't wrong yet. Submit rules ask "is this value present at all?" and treat
that same empty value as missing. Keeping those two questions on separate axes means a single
mistake never earns two error messages.

`DraftSubmitValidator<T>` is the base class that gives you this split without naming a
ruleset yourself: override `ConfigureDraftRules()` for the format checks and
`ConfigureSubmitRules()` for the completeness checks.

```csharp
public class ProfileForm
{
    public string DisplayName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
}

public class ProfileFormValidator : DraftSubmitValidator<ProfileForm>
{
    protected override void ConfigureDraftRules()
    {
        // Format rule: what a lenient draft save still enforces.
        RuleFor(p => p.Bio).MaximumLength(280).WithMessage("Bio is 280 characters max");
    }

    protected override void ConfigureSubmitRules()
    {
        // Completeness rule: what a submit demands, and what a draft save leaves alone.
        RuleFor(p => p.DisplayName).NotEmpty().WithMessage("Display name is required");
    }
}
```

## Which profile runs when

`FormidableForm`'s engine reads two profiles off `FormidableOptions`. `SubmitProfile` runs when
the form submits, defaults to `ValidationProfile.Submit`, and answers for the debounced refresh
that follows too — running only what a live pass has not already answered for the current edit
(see [Async validation](async-validation.md#the-refresh-runs-only-what-the-live-pass-did-not)).
`LiveProfile` runs on every field change, and defaults to `null`, meaning the submit profile
itself. So a live message says what a submit would actually complain about, presence rules and
all.

That does not turn a fresh form into a nag, because rule selection is not what holds a message
back. A live pass files a verdict only for the fields a committed change has notified the engine
about, so `DisplayName` above says nothing until the visitor has typed in it — and once they
have, clearing it again earns the message as soon as that edit commits, with no submit anywhere.
Set `LiveProfile` to narrow the selection when a submit rule is too expensive to run per change;
`ValidationProfile.Draft` leaves every submit-ruleset rule to the submit itself:

```csharp
var options = new FormidableOptions
{
    LiveProfile = ValidationProfile.Draft
};
```

Pass that instance through the form's `Options` parameter to point either moment at a
different profile — a wizard step, an approval stage — without touching the validator at all.

Saving a draft skips the form's submit pipeline entirely, whatever the live channel is
evaluating. It's a plain call against the same
validator you registered, taken into the page with `@inject IValidator<ProfileForm> Validator`
and handed the model plus the profile you want:

```csharp
var result = await Validator.ValidateAsync(_profile, ValidationProfile.Draft);
```

That overload is Formidable's, from `ValidatorProfileExtensions` in the core `Formidable`
namespace, and the profile is the only thing it adds. What comes back is FluentValidation's own
`ValidationResult` — `IsValid` and `Errors`, exactly as a direct call would return them — not a
Formidable type of any kind.

## Severity: not everything wrong should block

A rule doesn't have to block submission just because the model doesn't match it perfectly.
FluentValidation rules carry a severity: `.WithSeverity(Severity.Warning)` or
`.WithSeverity(Severity.Info)` marks a rule as advisory, and leaving it off makes the rule
an `Error`, exactly as it always was. Formidable maps that straight onto its own severity
— `Error` blocks submit, `Warning` and `Info` are shown to the user but never block it, so
a model that's all warnings and infos, with no errors, still submits successfully. That
mapping's `Error` leg includes an explicit `.WithSeverity(Severity.Error)` too: writing it
out by hand maps exactly the way leaving it off does — worth doing if your team would
rather every rule said its severity out loud.

```csharp
RuleFor(p => p.DisplayName)
    .Must(n => n.Length <= 40)
    .WithSeverity(Severity.Warning)
    .WithMessage("Display names over 40 characters may be truncated in some views");
```

Warnings and infos don't get a disclosure rule of their own — they surface precisely when an
error would. A field the visitor has committed a change to shows its advisory as soon as that
edit's live pass lands; everywhere else, a submit is what discloses one. From there, whatever's
showing keeps refreshing live while the visitor edits: it clears the moment they fix it, returns
if they break it again, and neither direction waits for a second submit.

**Next:** [Fields and collections](fields-and-collections.md)
