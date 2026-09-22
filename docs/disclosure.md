# Progressive disclosure

Formidable never shows an inline error for a field the user cannot currently see. The mechanism
is render-registration: a Formidable component that binds to a field — `FormidableInputText` and
other `ValidatedInputBase<TValue>` descendants, the renderless `FormidableField`, or the
registration-only `FieldAnchor` — registers that field with the form's `FieldRegistry` for as
long as it stays mounted, and unregisters when it leaves the render tree. Whether a field's issue
is visible follows directly from whether something registered it, which is a side effect of the
markup rendering it — not a flag anyone sets.

## How suppression works

At submit, the engine resolves every FluentValidation failure's property path to a
`FieldIdentifier` through the model introspector (see
[`docs/collections-and-row-identity.md`](collections-and-row-identity.md) for how that
resolution walks indexed paths), then asks the registry whether that identifier is currently
revealed. A field with no matching registration — because the markup that would render it sits
behind an `@if` that isn't satisfied — is unrevealed: the rule still ran and the issue still
exists in the validator's report, but it never reaches the `EditContext`'s message store,
`FieldMessage`, or `FormSummary`. It is suppressed instead, and `SuppressedIssueDiagnostic` (see
[`docs/options.md`](options.md)) is invoked once per suppressed issue so you can still observe it
outside the UI.

This registry check happens once, at the moment `ValidateForSubmitAsync` runs. The debounced
refresh pass that follows a submit re-validates but does not re-derive visibility from the
registry — it only narrows to fields that were already part of the visible set at that submit. A
field revealed after the fact stays quiet even if it is now failing; it surfaces at the *next*
submit, not the moment it renders.

Suppressing every failing field would let a submit silently do nothing, so a defensive gate
blocks that case with a form-level explanation instead:

```csharp
                    if (visibleErrors.Count == 0)
                    {
                        // Defensive gate: every failing field is hidden. Block anyway, with a
                        // form-level explanation instead of a silent no-op submit.
                        var gate = new ValidationIssue(
                            string.Empty,
                            "The form cannot be submitted because information that is not currently displayed is invalid.");
                        visibleErrors = [(gate, ModelLevelField)];
                    }
```

*Source: `src/Formidable.Blazor/FormValidationEngine.cs`*

## The two patterns

The disclosure sample teaches two different shapes, and they are not interchangeable — using the
wrong one either nags the user for fields they haven't reached, or makes a valid answer
unsubmittable.

### Pattern 1 — UI-gated sections

A section is collapsed by pure UI state that has nothing to do with the model. The rule behind
it is unconditional, so it always runs; whether it is *visible* depends entirely on whether the
user has opened the section:

```razor
    <p><button type="button" @onclick="() => _showDetails = !_showDetails">@(_showDetails ? "Hide" : "Show") traveler details</button></p>
    @if (_showDetails)
    {
        <p><label>Traveler name <FormidableInputText For="() => _request.TravelerName" @bind-Value="_request.TravelerName" /></label>
            <FieldMessage For="() => _request.TravelerName" /></p>
    }
```

*Source: `samples/Formidable.Sample/Pages/Disclosure.razor`*

The corresponding rule has no `.When(...)` at all:

```csharp
        RuleFor(t => t.TravelerName).NotEmpty().WithMessage("Traveler name is required");
```

*Source: `samples/Formidable.Sample.Shared/TravelRequest.cs`*

Submit while collapsed and the missing-name error is genuinely suppressed — logged to the
diagnostic, not shown inline. Expanding the section registers the field, but (per the refresh
rule above) the earlier suppressed error does not retroactively appear; submitting again is what
lets the now-registered field pick it up. Use this pattern for disclosure that is a pure UI
convenience — an expandable "more details" panel, a collapsed advanced section — where the rule
should count toward validity regardless of whether the user chose to look.

### Pattern 2 — data-gated cascades

A field's relevance is driven by another field's value, and the rule's `.When(...)` condition
mirrors the same condition the `@if` uses to render it:

```razor
    @if (_request.NeedsAccommodation == true)
    {
        <fieldset>
            <legend>Accommodation</legend>
            <label>Type
                <select value="@_request.AccommodationType" @onchange="OnTypeChanged">
                    <option value="">Choose…</option>
                    <option>Hotel</option>
                    <option>Serviced apartment</option>
                    <option>Accessible</option>
                </select>
            </label>
            <FieldAnchor For="() => _request.AccommodationType" />
            <FieldMessage For="() => _request.AccommodationType" />

            @if (_request.AccommodationType == "Accessible")
            {
                <p><label>Special requirements
                    <FormidableInputText For="() => _request.SpecialRequirements" @bind-Value="_request.SpecialRequirements" /></label>
                    <FieldMessage For="() => _request.SpecialRequirements" /></p>
            }
        </fieldset>
    }
```

*Source: `samples/Formidable.Sample/Pages/Disclosure.razor`*

```csharp
        RuleFor(t => t.AccommodationType).NotEmpty().WithMessage("Choose an accommodation type")
            .When(t => t.NeedsAccommodation == true);
        RuleFor(t => t.SpecialRequirements).NotEmpty().WithMessage("Describe the special requirements")
            .When(t => t.NeedsAccommodation == true && t.AccommodationType == "Accessible");
```

*Source: `samples/Formidable.Sample.Shared/TravelRequest.cs`*

Because the rule simply does not run unless `NeedsAccommodation == true`, there is never a
hidden failing issue to suppress in the first place — the model's own state keeps the rule and
the UI in lockstep, by construction. This is more than a nicety: without the matching `.When`,
answering "No" would leave `AccommodationType`'s unconditional rule permanently failing and
permanently unregistered — exactly the all-suppressed case the defensive gate exists for — and
the "No" data path could never submit. Mirroring the condition is what lets that alternate path
submit at all. Use this pattern whenever a field's relevance is determined by data, not by a
UI-only toggle.

## FieldAnchor for raw and foreign controls

Anything Formidable doesn't wrap — a plain `<input>`, a native `<select>`, a third-party
component — never registers on its own. `FieldAnchor` is a registration-only marker that renders
nothing; placing it next to a raw control keeps automatic disclosure truthful for it. The
`<select>` above is one example; the accommodation question's raw radio buttons are another,
always-rendered one:

```razor
    <fieldset>
        <legend>Do you need accommodation?</legend>
        <label><input type="radio" name="needs" checked="@(_request.NeedsAccommodation == true)"
                      @onchange="() => SetNeeds(true)" /> Yes</label>
        <label><input type="radio" name="needs" checked="@(_request.NeedsAccommodation == false)"
                      @onchange="() => SetNeeds(false)" /> No</label>
        <FieldAnchor For="() => _request.NeedsAccommodation" />
        <FieldMessage For="() => _request.NeedsAccommodation" />
    </fieldset>
```

*Source: `samples/Formidable.Sample/Pages/Disclosure.razor`*

Without the anchor, `NeedsAccommodation`'s `NotNull()` failure would be unrevealed forever — the
radio inputs are bound manually and no Formidable component ever mounts for that field.

## KeepRegistered and virtualization

Every registering component — `ValidatedInputBase<TValue>` descendants, `FieldAnchor`,
`FormidableField`, and `CollectionMessage` — exposes a `KeepRegistered` parameter. A container
such as `Virtualize` disposes rows that scroll out of view even though they remain part of the
form; without `KeepRegistered`, a scrolled-away row's field would unregister and its errors would
go quiet even though the row still exists in the model. Setting `KeepRegistered` on the row's
fields keeps them revealed after disposal, so scrolling never suppresses an error that was
already showing. See the Virtualize section of [`docs/component-kit.md`](component-kit.md) for
the full pattern.

## DisclosureOverride: the escape hatch

`FormidableOptions.DisclosureOverride` is consulted per issue *before* the registry check: return
`true` to force an issue visible regardless of registration, `false` to force it suppressed
regardless of registration, or `null` to defer to the registry as described above. It's the way
out for issues that don't fit the render-registration model — forcing a rule visible without
wrapping its field, or silencing a known-noisy rule outright. Server-applied issues
(`Engine.ApplyServerIssues`, see [`docs/server-integration.md`](server-integration.md)) bypass
the registry check entirely rather than defer to it — they're visible unless
`DisclosureOverride` explicitly returns `false`, since the server already validated the
submitted data and hiding a field the client happens not to have rendered isn't the concern
disclosure exists to solve.

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the
suppressed-issue list on the page accumulates across submits (it's append-only), so submitting
more than once shows the full history of what's been hidden, not just the latest submit's.
