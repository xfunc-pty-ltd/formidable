# CSS and accessibility

**You should already know:** how a Formidable form wires up end to end
([Quickstart](quickstart.md)).

Ship your own CSS and you own every consequence of it: browser default fights, a design system's
naming convention that has nothing to do with "valid" and "invalid," a dark-mode audit nobody
signed up for. A form library that ships CSS of its own trades that ownership away, quietly, the
day its class names collide with yours or its colours don't match your brand. A headless kit
means the classes stay yours: Formidable ships no CSS and no visual opinion (see
[Component kit](component-kit.md)). What it ships instead is a small, consistent surface of
class names and ARIA attributes. Every field exposes it the same way, whether it's Formidable's
own input, a renderless `FormidableField` template, or a plain `InputBase` sitting beside them.
So your own stylesheet and assistive technology both have one thing to key off, regardless of
which shape rendered the field.

## Need to know

One static rule computes a field's state class, shared by every Formidable component that
computes one:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Shared field CSS class rule: errors win, ungated; touched/modified without errors is warning
/// or info by the field's remaining advisory issues, and valid only when it is also known the
/// field would pass submit; pending appends while validating.
/// </summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined class string for a field state.</summary>
    public static string Compute(FieldState state, FormidableCssClasses classes) =>
        Assemble(
            state.HasErrors,
            state.IsTouched || state.IsModified,
            state.HasWarnings,
            state.HasInfos,
            state.WouldPassSubmit,
            state.IsValidating,
            classes);

    /// <summary>
    /// Joins the already-decided booleans into a space-joined class string: invalid wins outright
    /// and ungated; touched-or-modified gates every other tier, within which warning beats info
    /// beats plain valid — a field the user must still fix never reads as merely advisory, and an
    /// untouched, unmodified field earns no class at all regardless of what it carries. Valid
    /// alone carries one further requirement, <see cref="FieldState.WouldPassSubmit"/>: green is
    /// a promise about submit, so a clean-looking field whose submit-selected rules have no
    /// current answer — or whose current answer fails it undisclosed — wears no class rather
    /// than a confirmation it has not earned. The advisory tiers ignore that bit deliberately: a
    /// disclosed warning or info is a fact about the field regardless of what submit would say.
    /// Pending appends to whichever tier (or neither) applies. Private to <see cref="Compute"/>,
    /// its one caller — a Formidable input and <see cref="FormidableFieldCssClassProvider"/>'s
    /// native-input path both build a <see cref="FieldState"/> from their own sources and hand it
    /// to <see cref="Compute"/>, so this join happens in exactly one place for both.
    /// </summary>
    private static string Assemble(
        bool invalid, bool touchedOrModified, bool hasWarnings, bool hasInfos, bool wouldPassSubmit,
        bool pending, FormidableCssClasses classes)
    {
        var baseClass = invalid
            ? classes.Invalid
            : !touchedOrModified
                ? string.Empty
                : hasWarnings
                    ? classes.Warning
                    : hasInfos
                        ? classes.Info
                        : wouldPassSubmit
                            ? classes.Valid
                            : string.Empty;

        if (!pending)
        {
            return baseClass;
        }

        return baseClass.Length == 0 ? classes.Pending : $"{baseClass} {classes.Pending}";
    }
```

<!-- Source: `src/Formidable.Blazor/FormidableCss.cs` -->

In order: **errors win** — a field with error-severity issues is always `Invalid`, regardless of
touched/modified state. Failing that, touched-or-modified gates everything else: an untouched,
unmodified field earns no class at all, whatever it carries. A touched or modified, error-free
field is `Warning` when it has a warning-severity issue, `Info` when its only issues are
info-severity, and `Valid` once it has no issues left to show *and* the engine can say a submit
would not fail it. Finally, **pending appends** — whichever of those tiers applied (or none did)
gets `Pending` added onto it, space-joined, while a validation pass involving the field is in
flight. `Pending` never replaces the tier's own class, and it can appear on its own if the field
is validating before it's ever been touched.

Both halves of that `Valid` condition are deliberate. A field still carrying an advisory does not
read as `Valid`, because it still has something the user might act on. And green is a promise
about submit rather than a note that the visitor stopped by, so it waits until every rule the
submit profile selects has an answer for the value as it stands and none of those answers faults
the field. A failing answer the form has not disclosed yet counts, which is what stops an emptied
required box wearing a confirmation border. Whichever pass answered those rules last is the one
being read, and on the default profiles that is an ordinary live pass: `LiveProfile` defaults to
the submit profile, so the pass behind any committed change lands the answer green is asking
about, anywhere on the form.

The submit itself answers the same way, and so does the debounced refresh: the one behind every
post-submit edit, and the one behind any move in the rendered field set. That second one is partly
green's own. A row arriving or a virtualized panel scrolling
throws away the answers green was resting on, because the markup may have moved together with the
model and nothing announced the second half. Rather than blink, the engine holds the answer it
last gave and serves it until the pass that same move arms produces a new one. So markup moving is
not on its own something green reacts to. A value moving is, for that one field, and so is a model
that was changed alongside the markup without saying so, once the refresh lands and says which
fields it fails.

An edit opens a second gap, narrower than the first: the fields edited since the held answer was
computed lose it, each the moment its change commits, since theirs are the values that answer no
longer describes. Every other field keeps the answer it already had for as long as a fresh one is
demonstrably coming: a pass already validating, an open finite live-debounce window on a live
channel that runs the submit profile, or an armed post-submit refresh whose debounce is finite. A
window on a narrowed channel promises a pass that cannot answer, so it counts for nothing, and a
narrowed pass already in flight counts only for the refresh armed behind it; an infinite spelling
arms a timer that never fires, so a window that cannot close promises nothing either. One
keystroke does not blank every other field's
confirmation border for the gap behind it, debounce window plus the rule's own flight. The cover
is not indefinite. A pass still running past thirty seconds loses it, and a pass that ends without
landing (a fault, a cancellation) drops the held answer outright rather than riding out whatever
cover remained. A pass superseded by a newer one is not itself a drop, though: the answer then
stands or falls on whether the pass that displaced it, or something still armed, promises a fresh
one. Either gap closes the same way: the pass the hold was waiting on lands, and its own answer is
what green reads from there.

[`TrackFormValidity`](options.md#trackformvalidity)'s probe covers what is left: a form on which
nothing has happened at all, one that narrows `LiveProfile` past those rules, and a validator with
no rule-level seam, where a live pass is never taken as a coverage source. A page that calls
`DiscloseLoadedValuesAsync` answers the first of those itself, with no probe. Until one of them
has answered, a clean-looking field wears no tier class rather than a green one.

The five class names themselves are configurable, each with a default:

```csharp
namespace Formidable.Blazor;

/// <summary>Class names applied to a field based on its current <see cref="FieldState"/>.</summary>
public sealed class FormidableCssClasses
{
    /// <summary>Applied when the field has error-severity issues. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>
    /// Applied when the field is touched or modified, has no error-severity issues, and has at
    /// least one warning-severity issue. An error-severity issue on the same field wins
    /// <see cref="Invalid"/> instead, deliberately: a field the user must still fix should never
    /// read as merely advisory. Defaults to <c>"formidable-warning"</c>.
    /// </summary>
    public string Warning { get; set; } = "formidable-warning";

    /// <summary>
    /// Applied when the field is touched or modified, has no error- or warning-severity issues,
    /// and has at least one info-severity issue. An error- or warning-severity issue on the same
    /// field wins <see cref="Invalid"/> or <see cref="Warning"/> instead, deliberately: the more
    /// urgent severity always takes the class. Defaults to <c>"formidable-info"</c>.
    /// </summary>
    public string Info { get; set; } = "formidable-info";

    /// <summary>
    /// Applied when the field is touched or modified, has no error-, warning-, or info-severity
    /// issues, and the engine can vouch that a submit would not fail it
    /// (<see cref="FieldState.WouldPassSubmit"/>): green is a promise about submit, so a field
    /// whose submit-selected rules have no answer for the value as it stands — or an answer that
    /// fails it without showing why — wears no class rather than a confirmation it has not
    /// earned. Defaults to <c>"formidable-valid"</c>.
    /// </summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>Appended while a validation pass involving the field is in flight. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
```

<!-- Source: `src/Formidable.Blazor/FormidableCssClasses.cs` -->

`FormidableInputBase<TValue>`'s `CssClass` property (and `FormidableFieldContext.CssClass` for
the renderless path) calls `FormidableCss.Compute` with whatever `FormidableOptions.CssClasses`
instance the form's options currently hold, then merges the result with any consumer-splatted
`class`. See
[Component kit](component-kit.md) for the merge itself. The message components and
`FormidableSummary` use a related, fixed convention of their
own for the messages they render — see [Severity](severity.md) for the
`formidable-message--{severity}` and `formidable-summary__group--{severity}` class families, and
[Component kit](component-kit.md#heading-each-band) for the `formidable-summary__band--{severity}`
band each group sits inside.

That's the entire class-name contract: rename the five strings, and the rule above still
decides when each one applies. What follows is how to rename them, the fixed names on the
library's own markup, the rule a native `InputBase` picks up automatically, and the
ids/aria/focus wiring built on top of the same field state.

## Configuring `FormidableCssClasses`

`FormidableCssClasses` is a plain settings object — a mutable class with five `string`
properties (`Invalid`, `Warning`, `Info`, `Valid`, `Pending`, all shown with their defaults
above). Construct one, set whichever properties you want to rename to match your own stylesheet
or design system's naming convention, and assign it to `FormidableOptions.CssClasses`. That
property lives on the
options object every `FormidableForm<TModel>`/`FormidableValidator<TModel>` takes (see
[Options](options.md)). Formidable doesn't care what the strings are, only when each one
applies. The rule above is the entire contract. `CssClasses` is read at each class computation,
by kit components and by the provider that classes native `InputBase` components alike, so
renaming a class — by setting properties on the instance you already have, or by assigning a
whole new `FormidableCssClasses` — reaches both surfaces from the next computation each makes.
The `FormidableOptions` object around it is the thing that cannot be swapped: handing the form a
different instance on a later render throws rather than quietly changing nothing. See
[Options](options.md#formidableoptions-is-read-once) for that rule.

## The structural class inventory

The five configurable names cover what the engine computes onto elements *you* render. Every
class on an element the library itself renders is fixed: these names are contract, published
here so a stylesheet can key on any of them and know it is keying on something that will not
change. This is all of them — twenty-two names:

| Class | Where it renders |
|---|---|
| `formidable-message-list` | the persistent message list (`<ul>`) all three message components render — `FormidableFieldMessage`, `FormidableCollectionMessage`, `FormidableModelMessage` |
| `formidable-message` | every message item (`<li>`) in those lists |
| `formidable-message--error`, `formidable-message--warning`, `formidable-message--info` | appended to the item for its severity |
| `formidable-required` | the marker `<span>` `FormidableRequiredIndicator` renders |
| `formidable-summary` | `FormidableSummary`'s persistent wrapper |
| `formidable-summary__region` | every fixed-role live region the wrapper holds — which ones, `Show` decides |
| `formidable-summary__region--errors` | appended to the `role="alert"` region |
| `formidable-summary__region--advisories` | appended to the `role="status"` region |
| `formidable-summary__band` | one band per non-empty severity group |
| `formidable-summary__band--error`, `--warning`, `--info` | appended to the band for its severity |
| `formidable-summary__group` | the band's message list (`<ul>`) |
| `formidable-summary__group--error`, `--warning`, `--info` | appended to the group for its severity |
| `formidable-summary__heading` | the optional heading above a band's list |
| `formidable-summary__item` | each summary entry (`<li>`) |
| `formidable-summary__link` | the entry's click-to-focus button |
| `formidable-summary__overflow` | the list item holding what an `OverflowTemplate` renders for the entries `MaxItems` held back |

These names are deliberately not configurable, and that is a ruling rather than a gap: a
`FormidableCssClasses`-style seam for the structural names is additive later — it could arrive
without breaking anything — while the defaults above are what consumer stylesheets key on
regardless, so the names stay fixed vocabulary either way. What exists today is the splat: the
message lists and the summary wrapper accept unmatched attributes, so utility classes and data
hooks reach the containers, with a splatted `class` merging ahead of the structural name. The
fixed-role regions and everything inside them, the message items, and the required marker take
no splat; they are what the table freezes.

Several of those names carry a severity, and severity is one thing a stylesheet should not say in
colour alone. An inline message item is its own text plus two classes: `formidable-message`, and
one of `--error`, `--warning` or `--info`. That modifier is the only thing on the item saying
which severity it is, so amber against red is the whole difference between "you have to fix this"
and "worth knowing" for a reader who sees the two as one colour. `FormidableSummary` can put that
difference into words — `ErrorsHeading`, `WarningsHeading` and `InfosHeading` label each band in
your own wording, with no English default standing in for them — while an inline list has no such
parameter. Its cue has to come from the stylesheet or from the message text: a word ahead of the
message, an icon with a text alternative, anything visible that survives being read in one colour.
`formidable-valid` and `formidable-pending` are the same question with less to fall back on:
`formidable-invalid` at least pairs with the input's `aria-invalid` and with the message that
explains it, while the kit renders no text for a confirmed field or a checking one, and no
`aria-busy` anywhere. Severity, confirmed and checking each want a cue of their own; which cue,
and how it is worded, is yours.

## The `FieldCssClassProvider` bridge

A field rendered by a plain Blazor `InputBase` (not a Formidable component) still needs a class
that reflects its validation state, and `InputBase` gets its class from the `EditContext`'s
`FieldCssClassProvider`, not from anything Formidable's own components compute. The engine
installs a Formidable-aware provider on the `EditContext` at construction so a native input picks
up the same configured class names automatically:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Internal fast-path reads engine-adjacent components need without growing the public
/// <see cref="IFormidableEngine"/> contract for what only they want:
/// <see cref="FormidableFieldCssClassProvider"/> reads <see cref="IsFieldValidating"/>,
/// <see cref="IsFieldTouched"/>, <see cref="FieldAdvisories"/>, and
/// <see cref="WouldPassSubmit"/> to build the same
/// <see cref="FieldState"/> bits <see cref="IFormidableEngine.GetFieldState"/> would, without
/// paying for <c>IsModified</c> or the error scan it already gets from the <c>EditContext</c>
/// directly; any component that renders a message list reads <see cref="InlineMessageLive"/> and
/// passes it to the shared list renderer, which adds the <c>aria-live</c> attribute when the value
/// is not null. <see cref="FormidableEngine{TModel}"/> implements this explicitly; any other
/// <see cref="IFormidableEngine"/> (a test double, say) does not, so each reader falls back
/// to its own default for whichever member it needs.
/// </summary>
internal interface IValidatingFieldReader
{
    /// <summary>Whether a validation pass currently in flight covers <paramref name="field"/>.</summary>
    bool IsFieldValidating(FieldIdentifier field);

    /// <summary>Whether <paramref name="field"/> has been marked touched.</summary>
    bool IsFieldTouched(FieldIdentifier field);

    /// <summary>
    /// Whether <paramref name="field"/> currently has a warning-severity issue and whether it
    /// currently has an info-severity issue, read together in one pass over its issues rather
    /// than two separate ones.
    /// </summary>
    (bool HasWarnings, bool HasInfos) FieldAdvisories(FieldIdentifier field);

    /// <summary>
    /// Whether the engine can vouch that a submit would not fail <paramref name="field"/> — the
    /// <see cref="FieldState.WouldPassSubmit"/> conjunct the Valid class requires, answered
    /// without building the rest of a <see cref="FieldState"/>.
    /// </summary>
    bool WouldPassSubmit(FieldIdentifier field);

    /// <summary>The configured <see cref="FormidableOptions.InlineMessageLive"/>, or null.</summary>
    string? InlineMessageLive { get; }
}

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes. Which tier a touched-or-modified, error-free field earns — Warning, Info, or Valid —
/// is the same decision <see cref="FormidableCss.Compute"/> makes for a Formidable input, not a
/// second one that happens to agree.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly IFormidableEngine _engine;
    private readonly IValidatingFieldReader? _reader;

    /// <summary>
    /// Creates a provider reading class names, touched, pending, and advisory state from
    /// <paramref name="engine"/>. Construction is a consumer's business only when their own
    /// <c>EditContext.SetFieldCssClassProvider</c> call has replaced the installed one and they
    /// want Formidable's classes back, or when their own provider wants to delegate to this one:
    /// pass the engine, reachable through <see cref="FormidableFormContext.Engine"/>.
    /// </summary>
    /// <remarks>
    /// The class names are the engine's, deliberately, and there is no overload taking a
    /// different set: a form whose native inputs answered with names its kit inputs did not
    /// would be reporting the same field state two ways. A consumer who genuinely wants a
    /// different map has the whole rule in public API — <see cref="FormidableCss.Compute"/> over
    /// <see cref="IFormidableEngine.GetFieldState"/> — and writes their own provider.
    /// </remarks>
    public FormidableFieldCssClassProvider(IFormidableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _reader = engine as IValidatingFieldReader;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        bool touched, pending, hasWarnings, hasInfos, wouldPassSubmit;
        if (_reader is not null)
        {
            touched = _reader.IsFieldTouched(fieldIdentifier);
            pending = _reader.IsFieldValidating(fieldIdentifier);
            (hasWarnings, hasInfos) = _reader.FieldAdvisories(fieldIdentifier);
            wouldPassSubmit = _reader.WouldPassSubmit(fieldIdentifier);
        }
        else
        {
            var fallback = _engine.GetFieldState(fieldIdentifier);
            touched = fallback.IsTouched;
            pending = fallback.IsValidating;
            hasWarnings = fallback.HasWarnings;
            hasInfos = fallback.HasInfos;
            wouldPassSubmit = fallback.WouldPassSubmit;
        }

        var state = new FieldState
        {
            IsTouched = touched,
            IsModified = editContext.IsModified(fieldIdentifier),
            IsValidating = pending,
            HasErrors = editContext.GetValidationMessages(fieldIdentifier).Any(),
            HasWarnings = hasWarnings,
            HasInfos = hasInfos,
            WouldPassSubmit = wouldPassSubmit
        };

        // Read at each computation, not held from construction: the options object is what a
        // consumer reaches for to rename a class, and a provider holding the instance it was
        // built with would leave native inputs answering with the old names while kit inputs,
        // which read through the options at each render, answered with the new ones.
        return FormidableCss.Compute(state, _engine.Options.CssClasses);
    }
}
```

<!-- Source: `src/Formidable.Blazor/FormidableFieldCssClassProvider.cs` -->

```csharp
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(this));
```

<!-- Source: `src/Formidable.Blazor/FormidableEngine.cs` -->

This is the same class names as `FormidableCss.Compute`, and genuinely the same rule: the provider
builds its own `FieldState` — `IsModified` and `HasErrors` read straight off the `EditContext`,
`IsTouched`, `IsValidating`, `HasWarnings`, `HasInfos`, and `WouldPassSubmit` read from the
engine — and hands it to `FormidableCss.Compute`, the one place the
invalid/warning/info/valid/pending decision is made. Concretely, a field the engine considers
touched (`FieldState.IsTouched`, set by `MarkTouched()`) reaches the `Valid` tier through this
path on exactly the terms a Formidable input reaches it on, `WouldPassSubmit` included, even
before the `EditContext` has ever seen `NotifyFieldChanged` for it — `FormidableCss.Compute`'s
`IsTouched || IsModified` branch (see above) is the one decision both paths share, not two
decisions that happen to agree. The provider probes the engine for `IValidatingFieldReader`, an
internal fast path `FormidableEngine<TModel>` implements for every one of those engine-sourced
reads, and falls back to `GetFieldState(fieldIdentifier)` for all of them at once when an
`IFormidableEngine` doesn't implement it (a test double, say) — the probe is a single `as`
check, not a per-member one, so there's no in-between case where some reads have the fast path and
others don't. A `FieldState` built without an engine defaults `WouldPassSubmit` to `true`, so a
hand-rolled provider or a test double keeps the `Valid` tier reachable rather than losing it to a
bit it never set. A native input inside a Formidable form shows the
same "checking…" cue a Formidable input does, automatically; see the Vanilla interop section of
[Component kit](component-kit.md) for the provider wired into a native `InputText` beside a
Formidable one.

Installation is the engine's job, so a form never constructs a provider to get these classes. The
constructor is public for the case where an `EditContext` no longer has Formidable's provider on
it: `SetFieldCssClassProvider` holds exactly one, so a consumer's own call replaces it, and the
way back is `new FormidableFieldCssClassProvider(engine)` with the engine read from
`FormidableFormContext.Engine`. The same construction lets a consumer's provider delegate to
Formidable's and append classes of its own to what it returns. It takes the engine and nothing
else, so the names it applies are always the ones the form's own kit inputs apply; a provider
that should answer with a different map is a provider of your own, built out of the same two
public pieces this one uses — `FormidableCss.Compute` over `IFormidableEngine.GetFieldState`. One lifetime note for
`FormidableValidator`, which attaches to an `EditContext` it doesn't own: disposing the validator
leaves Formidable's provider installed on that `EditContext`, still pointing at the disposed
engine, so a page that keeps using the `EditContext` afterwards should install whichever provider
it wants for that next life.

## Accessibility wiring

### Deterministic ids

Every id Formidable assigns — an input's `id`, a message list's `id`, and the target the focus
service looks for — comes from one function, keyed by the owning object instance and the field
name together:

```csharp
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{NameHash(field.FieldName):x8}-{name}";
    }

    /// <summary>
    /// FNV-1a over the name's UTF-16 code units. Spelled out rather than delegated: see the
    /// remarks on <see cref="For(FieldIdentifier)"/> for why a per-process hash cannot serve here.
    /// </summary>
    private static uint NameHash(string name)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in name)
        {
            unchecked
            {
                hash = (hash ^ (byte)c) * prime;
                hash = (hash ^ (byte)(c >> 8)) * prime;
            }
        }

        return hash;
    }
```

<!-- Excerpt from `src/Formidable.Blazor/FormidableFieldId.cs` -->

The shape is `formidable-{owner-hash}-{name-hash}-{sanitized-name}`, and the name reaches it
twice because each trip does a different job. Sanitizing lowercases letters and digits and turns
everything else into `-`, which is what makes an id legible and selectable: `Url`, `URL` and
`url` all end `-url`, so a stylesheet or a browser locator can say `[id$='-url']` and mean it.
That legibility is also a collision, since those are three different fields, and two elements
sharing a DOM id is invalid HTML: both `aria-describedby` values point at one message list, and a
click-to-focus reaches whichever element came first. The hash of the original name, case and
punctuation intact, is what separates them again.

It sits in front of the sanitized name rather than after it. The owner segment is an
object-identity hash, which is opaque rather than addressable: it names the instance, so a
different model or a different collection row gives a different one (all but certainly; the next
paragraph is the exception), and its value is drawn from a sequence the runtime advances on each
first identity-hash request, so anything the app asks about earlier moves it along. Nothing you
write can name it. The suffix is what stays put, and putting the name anywhere but last would
take that away. The name hash itself is spelled out in the library rather than taken from
`string.GetHashCode()`, which — unlike the identity hash beside it — really is randomized per
process, so an id computed in your test run would not match the one the app renders. Thirty-two
bits over the names one object owns makes two ids overwhelmingly likely to differ rather than
certain to; the sanitizer collision it replaces was certain.

The owner segment prints in the same eight hex digits and is narrower than it looks. The runtime
keeps an object's identity hash in part of the object's header rather than in a full `int`
(twenty-six bits of it on CoreCLR, the runtime under Blazor Server and every server-side render),
so among enough owner objects rendered at once two can draw the same value, and their same-named
fields then render the same id. The odds climb with the square of the count: negligible for the
hundreds of rows a form usually shows, under one percent at a thousand rendered at once, about one
in six at five thousand. Validation is untouched, because the engine tells fields apart by the
owner reference and never by this hash. What a duplicate id disturbs is whatever is keyed by the
id. A site that reaches an element through it reaches the first of the two in the document: a
summary click or a blocked submit's focus lands on the first row, the second row's
`aria-describedby` names the first row's message list, and the value sync on blur writes into the
first row's box. The field-order service keys its answer by id as well, and there a shared id can
name only one field: the one the registry lists later keeps it and takes the first row's place in
the resolved order, and the other never enters that order, so its issues sort after every placed
field's. Virtualizing a form that large keeps the rendered set to the rows in view, which is the
count that matters.

The model-level field — an empty `FieldIdentifier.FieldName`, the one the defensive
all-suppressed gate in [Disclosure](disclosure.md) targets — gets `form` as its name
segment rather than an empty one. `FormidableForm` renders it on its own `<form>` element (see
[Component kit](component-kit.md)), so a page only reaches for `FormidableFieldId` by hand for a
field that owns no input of its own — a collection container, or the `<form>` element in attach
mode. An overload of `For` takes a member-access expression instead of a `FieldIdentifier` for
that case — `FormidableFieldId.For(order, o => o.Description)` — so the id can be computed
without a `nameof` step to keep in sync with the property it names. It evaluates the object part,
the way `FieldIdentifier.Create` evaluates its own, so `o => o.Address.City` names the field the
`Address` instance owns, which is the one a component renders. There is no expression shape
for the model-level field itself; construct `new FieldIdentifier(model, string.Empty)` and pass
it to the `FieldIdentifier` overload, as `FormidableForm` does internally.

The owner segment separates instances, not roots. Two roots over one model instance — two
`FormidableForm`s, or a `FormidableForm` and a `FormidableValidator` — compute identical ids for
every field they both render, which duplicates ids the same way a sanitizer collision would: both
inputs point `aria-describedby` at one message list, and click-to-focus reaches whichever element
came first. Two `FormidableForm`s duplicate the `<form>` element's own id whatever else they
render, since each puts the model-level field's id there. Bind each root to its own model
instance.

The message list's id is the same id with `-messages` appended, and `FormidableFieldId.MessagesFor`
is the one place that appends it — the `aria-describedby` contract has an owner rather than a
convention. Call it when wiring a control by hand; the kit's inputs, the message components and
`FormidableFieldContext.AriaDescribedBy` all get their string from it.

### `aria-invalid` and `aria-describedby`

`FormidableInputBase<TValue>.AddCommonAttributes` renders both of these, following the same field
state the CSS class rule reads. It renders one more attribute the excerpt leaves out —
`aria-required`, which follows what the submit profile's rules demand of the field rather than
anything the field's current state is doing:

```csharp
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", ComputeAriaDescribedBy());
        }
```

<!-- Source: `src/Formidable.Blazor/FormidableInputBase.cs` -->

`aria-invalid="true"` only appears for error-severity issues; `aria-describedby` appears for any
issue, warnings and infos included, since those are still rendered and still worth announcing.
`ComputeAriaDescribedBy()` is the merge with whatever the consumer splatted: a persistent hint's
own `aria-describedby` keeps its place at the front, and the messages id is appended after it
while issues exist — the same consumer-first policy as the `class` merge, so an error joining
the field extends the announced sequence instead of replacing the hint. With nothing splatted,
the value is the messages id alone: `FormidableFieldId.MessagesFor(field)`, the field's id plus
`-messages` — exactly the id a field's own message list carries, whether
`FormidableFieldMessage` or `FormidableCollectionMessage` rendered it:

```csharp
        builder.OpenElement(sequence++, "ul");
        builder.AddMultipleAttributes(sequence++, additionalAttributes!);
        builder.AddAttribute(sequence++, "id", listElementId);
        builder.AddAttribute(sequence++, "class", FormidableCss.CombineClassNames(additionalAttributes, "formidable-message-list"));
```

<!-- Source: `src/Formidable.Blazor/FormidableFieldMessage.cs` -->

The id enters the render tree after any consumer-splatted attributes, so it wins the
duplicate-attribute race: an `id` splatted onto a message component is ignored, because this one
is the contract every `aria-describedby` on the page relies on. The splatted `class` merges with
`formidable-message-list` instead of being replaced, the same policy the inputs apply to their
state class.

`FormidableFieldContext` — the renderless path's equivalent — computes the identical pair from
the same inputs, and reports the field's requirement beside them, so a hand-rolled control driven
by `FormidableField` gets the same wiring a `FormidableInputBase` descendant does. One deliberate
difference: `AriaDescribedBy` here is always the single messages id, never a merged list,
because the consumer composes the markup themselves — a control that also carries a hint writes
the hint's id and `field.AriaDescribedBy` into the attribute in that order by hand:

```csharp
        AriaInvalid = state.HasErrors;
        AriaDescribedBy = issues.Count > 0 ? FormidableFieldId.MessagesFor(elementId) : null;
```

<!-- Source: `src/Formidable.Blazor/FormidableFieldContext.cs` -->

`aria-required="true"` follows a different question from the other two. `aria-invalid` and
`aria-describedby` describe what the field's values are currently doing; `aria-required` describes
what the submit profile's rules demand of it, so it appears while
`IFormidableEngine.GetFieldRequirement` reports `FieldRequirement.Required` and is not touched
by any validation pass. That is why it is asked separately from the single state-and-issues read
the rest of `AddCommonAttributes` works from: the derived answer is reused, so asking per field per
render is a lookup. [`RequiredOverride`](options.md#requiredoverride) is the part that can change
on its own, so it alone is invoked on every ask rather than cached with the rest.

It is `aria-required` rather than the native `required` attribute, and that is a deliberate
choice rather than an oversight. `required` makes every empty required field match `:invalid`
from first paint — the opposite of the untouched-fields-stay-quiet discipline the state classes
above follow, and untouched by `novalidate`, which switches off interactive validation rather
than the constraint computation. It also arms the browser's own submit-time enforcement:
`FormidableForm`'s default `novalidate` (see
[Component kit](component-kit.md#formidableformtmodel)) keeps that from firing, but in a form
without it — attach mode's consumer-owned `EditForm`, or a splat that removed the default — the
browser refuses the submit before Formidable's pass ever runs and puts its own bubble in front
of the message the form was going to show, in the browser's wording and the browser's placement.
`aria-required` states the same fact to assistive technology and leaves the verdict where the
rest of the form's verdicts live.

The visible mark and the announced fact are deliberately separate elements.
[`FormidableRequiredIndicator`](component-kit.md#formidablerequiredindicatortvalue) draws the mark
as `<span class="formidable-required" aria-hidden="true">`, and the input beside it carries
`aria-required="true"`. Splitting them is what stops a screen reader announcing the field as
"Title star": an `aria-hidden` subtree is excluded from the accessible name computed from the
label around it, so the mark can sit inside the `<label>` — where sighted readers expect it —
without changing the input's name by a character. Style the mark through
`formidable-required`; the library ships no styling, and
[`RequiredIndicatorContent`](options.md#requiredindicatorcontent) supplies the text inside it (an
empty string keeps the empty element for a glyph drawn with `::before`). Turning
[`ShowRequiredIndicators`](options.md#showrequiredindicators) off removes the mark and leaves
`aria-required` in place, because whether a value is demanded is a fact about the input rather
than a decoration.

An input the page renders itself owes `aria-required` and `aria-describedby` by hand, since
neither arrives from a field context it never had: `GetFieldRequirement(field)` answers the first,
and the second carries a condition of its own, below. `aria-invalid` is the one a Blazor
`InputText` sees to itself, and it does that from the field's messages rather than from anything
the markup says. It recomputes every time its parameters are set and every time validation state
changes, and each time it reads the splat it is holding right then rather than the markup. Holding
the key while the field has messages, it leaves the held value alone: a `"false"` stands, and so
does a `null`, which the Razor compiler still counts as naming the attribute and which renders
nothing. Holding no key, it writes `aria-invalid="true"`. With no messages on the field it removes
the key and writes nothing, whatever the markup says.

The removal is what puts render order in charge, since it empties the component's own copy rather
than the markup: the page's value returns at the next render of the page around the input, and not
before. Until that render a page naming the attribute behaves exactly like one that does not, so a
validation-state change arriving on its own puts `"true"` on the input over whatever the page
asked for. An edit is not the case to watch, because its change event runs through the page and
re-renders it. Formidable's other passes do not: a debounced live pass, an async rule landing, the
post-submit refresh and a background `ApplyServerIssues` all move validation state with no render
of the page in the loop. What supplies one is a subscription a hand-rendering page already wants —
`Engine.StateChanged` keeps an engine-derived attribute fresh, and the render it triggers is also
what puts the page's own `aria-invalid` back. Without it the framework's answer is the one that
stands.

`/vanilla` and `/workout` name the attribute and answer `"true"` or nothing from
`GetFieldState(field).HasErrors`, which is the same pair of answers the framework writes, so the
ordering cannot put a third thing on the input; both take out that subscription. `/attach` names
nothing and takes the framework's.

`aria-describedby` carries a condition a kit input paired with a `FormidableFieldMessage`
never meets: it has to name an element that is on the page, and a native `ValidationMessage`
renders one `<div>` per message and nothing at all when there are none, where
`FormidableFieldMessage` renders its list empty and leaves it standing. So a page
pointing at a native message element renders the attribute only while that element exists, and
the question to ask is the one that component itself answers —
`EditContext.GetValidationMessages(field)`. Every sample pairing a hand-rendered input with a
native `ValidationMessage` does it that way: `/workout`'s venue input, `/attach`'s "Submitted
by", and `/vanilla`'s nickname.

The attribute renders whether or not its target actually exists. A kit input points
`aria-describedby` at the field's messages id only while the field carries issues;
`FormidableForm` points its `<form>` element's `aria-describedby` at the model-level messages id
unconditionally, whether or not the model has anything to say. Either id resolves only where the
matching component is actually placed — a field's `FormidableFieldMessage` or
`FormidableCollectionMessage`, or, for the form, a `FormidableModelMessage`. Leave the component
out and the attribute still renders, naming an id nothing on the page carries. That is not a bug
to chase down: WAI-ARIA 1.2 §8.6.1 tells user agents to ignore a reference that resolves to
nothing, and ARIA 1.3 permits an author to leave one dangling too, on the general ground that a
modern page's DOM can be populated when necessary, not because it names this exact case. The real
cost sits elsewhere — a scanner cannot tell whether the reference was meant to resolve, so
axe-core reports it as `needs review` at `impact: critical`, once per run for each element whose
reference resolves to nothing, rather than as a violation. Add the matching component and the
finding disappears with it.

### `FormidableSummary` as a live region

A live region announces reliably only when the element carrying the role was in the DOM before
the content arrived: assistive technology is inconsistent about a role that enters, or changes,
in the same render as the text it should announce — and the announcement that matters most, the
first blocked submit, is exactly the one that shape most plausibly drops. `FormidableSummary` is
built so that moment cannot depend on it. Its persistent wrapper holds fixed-role region
elements that render from the first paint and stand empty until there is something to say:
`formidable-summary__region--errors` carrying `role="alert"`, and
`formidable-summary__region--advisories` carrying the politer `role="status"`. No role on any
element ever changes, and every issue that arrives after a region's own first render inserts into
a live region whose role was already there:

```csharp
    // One fixed-role region: the element and its role render whether or not any issue currently
    // matches, so a band arriving later inserts into a live region assistive technology has
    // already been told about — a role, once in the DOM, never changes, and the element carrying
    // it outlives every band that comes and goes inside it. Show is what decides a region exists
    // at all, so a runtime Show change is where a region and its first band still share a render.
```

```csharp
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", regionClass);
        builder.AddAttribute(sequence++, "role", role);
        builder.AddAttribute(sequence++, "aria-atomic", "false");
```

<!-- Excerpt from `src/Formidable.Blazor/FormidableSummary.cs` -->
Elided in between are the rest of
that comment, `BuildRegion`'s signature, and its counter's initialisation; the class and role each
call hands it are fixed at the call site, and each call is wrapped in its own sequence-number
region so the element the role sits on is the same DOM node across renders (see
[Component kit](component-kit.md#formidablesummary) for that half of the wiring).

`aria-atomic="false"` is the same argument one level down. `status` and `alert` are atomic by
DEFAULT — each role carries an implicit `aria-atomic` of true — so a region left to itself
announces the whole of itself again on every change: fix one field on a form blocked by five and
the visitor is read the four that remain, assertively, before they reach the next box. (A bare
`aria-live`, which is what [`InlineMessageLive`](options.md#inlinemessagelive) puts on a message
list, carries no such implication and needs no such correction.) Spelling the attribute out
narrows each announcement to the entries that actually changed. The persistent element is what
makes that possible in the first place: the entries come and go inside a region that stays, so
there is a difference between the region and its parts for `aria-atomic` to be about.

Entries carry a key for the same reason, and it is a rendering decision with an accessibility
consequence. Blazor matches unkeyed siblings by position, so correcting the field the first entry
names would rewrite the text of every entry below it rather than removing that one — to a screen
reader on a non-atomic region, a band that churns wholesale is a band that announces wholesale.
Keying each entry by the issue it carries makes a removal read as a removal.

The component subscribes to the engine's `StateChanged` event itself, so a region's content — and
whatever it announces — stays current through every kind of update, not just the moment of
submit: a submit that fails, a live-typed correction that clears an error, a server-applied issue
landing. Blocking problems and commentary announce at different urgencies by construction, since
errors band into the `alert` region and warnings and infos into the `status` one; a follow-up
edit that clears the last error empties the assertive region rather than changing what any
element is.

The regions are per summary, not per form, which matters as soon as a page splits the bands with
`Show` (see [Component kit](component-kit.md#showing-one-severity-band)): `Show` decides which
regions a summary renders, so an `Errors` summary carries only the `alert` region and an
`Advisories` one only the `status` region. A submit that produces both kinds announces from both
regions — the blocking half assertively, the advisory half politely — and that is equally true of
one combined summary and a split pair, which render the same two regions between them. What `All`
saves is coordination, not announcements: one summary cannot double-list an issue the way a
default summary rendered beside a filtered one can.

Because `Show` is a parameter, changing it at runtime is where a region's persistence has its
boundary. A region added while matching issues are already on screen renders together with its
first band; only what arrives afterwards lands in a region the DOM already held. A `Show` fixed
in the markup, which is the usual case, never reaches that.

### Focus service

Every entry in `FormidableSummary` is a button that calls `IFormidableFocusService.FocusAsync`, which
locates and focuses the DOM element carrying a field's deterministic id. The summary is not its
only caller: `FormidableForm` moves focus through the same service on every blocked submit, unless
`FocusFirstErrorOnInvalidSubmit="false"` or the invalid-submit handler says otherwise, and on
every move a page asks for by calling `FocusFirstErrorAsync()` (see
[Component kit](component-kit.md#formidableformtmodel)). It aims at the first error rather than the
first visible issue, because issue order follows the page and the topmost field may be carrying
only a warning: a keyboard visitor whose submit was refused should arrive at the thing that refused
it, not at an advisory above it. In the rare case where a submit blocks with no error on screen at
all, it falls back to the first visible issue, so focus still moves rather than being left wherever
the submit button was. Both callers read a miss off the same signal, which the service's own
contract explains:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Moves keyboard focus (and scrolls into view) to the DOM element rendered for a field.
/// The target element is located by its <see cref="FormidableFieldId"/> id, which the kit's
/// inputs assign automatically — any element carrying that id can be focused this way.
/// </summary>
/// <remarks>
/// Implementing this interface — a recording double in a bUnit test, a focus behaviour of
/// your own — is supported surface, and it grows accordingly: a member added after v1
/// carries a default implementation that does nothing and reports having done nothing, the
/// answer <see cref="FocusAsync"/> already gives whenever nothing took focus.
/// Moving focus is a courtesy, so an implementation that does not override the addition
/// declines it and leaves the page as it was.
/// </remarks>
public interface IFormidableFocusService
{
    /// <summary>
    /// Moves focus to the rendered element for <paramref name="field"/>. The scroll that brings it
    /// into view prefers the field's message list when one is rendered, so clicking a
    /// collection-level issue shows the message that was clicked rather than the middle of the
    /// group; it falls back to the focus target itself when no message list exists. Returns
    /// <c>true</c> when the element took focus and <c>false</c> when nothing did. There are two
    /// routes to <c>false</c> and they reach the caller as one answer, because the visitor is in
    /// the same place on either: no element carries the field's id — a virtualized row outside
    /// the render window, a control that renders no such id at all — or the element that carries
    /// it will not take focus, being disabled, hidden, not a focusable kind of element, or sealed
    /// off by an ancestor such as a closed <c>&lt;details&gt;</c>, an <c>inert</c> subtree, or a
    /// native <c>&lt;dialog&gt;</c> open elsewhere on the page. An element merely covered by an
    /// overlay does take focus, so that shape answers <c>true</c>.
    /// </summary>
    /// <param name="field">The field whose rendered element should receive focus.</param>
    ValueTask<bool> FocusAsync(FieldIdentifier field);
}
```

<!-- Source: `src/Formidable.Blazor/IFormidableFocusService.cs` -->

The shipped implementation is a thin JS-interop wrapper: it computes the field's
`FormidableFieldId` for the focus target and its `MessagesFor` id for the scroll target, and
passes both id strings across the interop boundary, returning whatever the JS side reports:

```csharp
    public ValueTask<bool> FocusAsync(FieldIdentifier field) =>
        Module.InvokeAsync<bool>(
            "focusField",
            FormidableFieldId.For(field),
            FormidableFieldId.MessagesFor(field));
```

<!-- Source: `src/Formidable.Blazor/FormidableFocusService.cs` -->

```javascript
export function focusField(id, scrollId) {
    const element = document.getElementById(id);
    if (!element) {
        return false;
    }

    const scrollTarget = (scrollId && document.getElementById(scrollId)) || element;

    // Centring works for an input, but a tall container's centre is the middle of its
    // contents, which is nowhere near the message that was clicked. 60% is comfortably above
    // an ordinary field wrapper and comfortably below a container spanning the viewport, so it
    // separates the two without being sensitive to small layout changes.
    const tall = scrollTarget.getBoundingClientRect().height > window.innerHeight * 0.6;
    // "auto" hands the motion to each scrolling box's own scroll-behavior, which is where a
    // visitor's prefers-reduced-motion can reach it. Naming "smooth" here would animate the
    // scroll whatever the page and the visitor asked for, and how a page moves is styling,
    // which this library does not ship.
    scrollTarget.scrollIntoView({ behavior: "auto", block: tall ? "start" : "center" });
    element.focus({ preventScroll: true });
    // Whether the element took focus, not merely whether it was found. An element can be on the
    // page and still refuse focus, and that field is the whole reason the caller has a recovery
    // path: a fallback that makes the target reachable and retries, or a diagnostic when none is
    // wired. Reporting "found" as "focused" is what leaves that path unreachable, so the answer is
    // read back from the document rather than assumed from the call.
    return document.activeElement === element;
}
```

<!-- Source: `src/Formidable.Blazor/wwwroot/formidable.js` -->

Focus and scroll come apart there, deliberately. Focus always lands on the field's own element,
because that is what a keyboard visitor has to be able to type into. The scroll prefers the
field's message list, so an issue with no input of its own — a collection-level rule, whose
element is the container holding every row — brings the message that named the problem into view
instead of the middle of the group. Then the target's own height decides the alignment: taller
than 60% of the viewport and it aligns to its top, smaller and it centres. Centring is right for a
field wrapper and wrong for a container that fills the screen, whose centre is somewhere down
among its rows.

**How** it moves is yours. `behavior: "auto"` means each scrolling box the move touches uses its
own `scroll-behavior`, so a page that says nothing gets an instant jump and a page that asks for
`scroll-behavior: smooth` gets an animation — and a visitor who has asked their system for less
motion is answered by the same stylesheet, through a media query the library is in no position to
write:

```css
html {
    scroll-behavior: smooth;
}

@media (prefers-reduced-motion: reduce) {
    html {
        scroll-behavior: auto;
    }
}
```

Put it on the root element: `scroll-behavior` propagates to the viewport from `html` and, unlike
`overflow`, not from `body`. A scrollable container of your own — a panel holding a `Virtualize`,
say — is a second scrolling box and needs its own declaration, since a focus move inside one
scrolls the container as well as the page. Write the guard rather than assuming it: browsers
differ on whether `scroll-behavior: smooth` reads the preference by itself, and Chromium does not,
so without the media query a visitor who asked for less motion gets the animation anyway. Watch
what else the declaration catches, too — assigning `scrollTop`, or `scrollTo` without an explicit
`behavior`, animates once the box is smooth, which is rarely what a programmatic jump wants.

`document.getElementById(id)` locates both targets, and a miss on the focus target is reported
rather than swallowed: `focusField` answers `document.activeElement === element`, so it returns
`false` both when no element carries the focus id and when the element carrying it did not take
focus, and `FocusAsync` propagates that bool straight back to its caller. Reading the answer back
rather than assuming the call took is what makes the second case recoverable at all: an element
that is present and refuses focus would otherwise be reported as a move that landed, and the
seams below would never see it. A miss on the scroll id alone is
not an error: it falls back to the focus element itself, which is always the size-aware scroll's
minimum viable target. What happens next diverges by caller. `FormidableSummary`'s click-to-focus
handler no-ops silently when `FocusAsync` reports a miss and no `FocusFallback` is set (or the
fallback itself fails to recover it). The form's own moves read the identical miss
and retry it through its own `FocusFallback` parameter, the same name and delegate shape as the
summary's and typically wired to the same callback. With none wired, though, they do not share the
summary's silent no-op: a visitor sent to a field nobody clicked has nowhere else to land, where a
summary click simply has no effect, so the form reports a diagnostic instead (see
[Component kit](component-kit.md#focusfallback)).

The gap `FocusFallback` recovers, on either component, matters for three cases documented
elsewhere.
The first is a field scrolled out of a `Virtualize` window with no current DOM element: the focus
miss `FocusFallback` exists to recover from (see [Component kit](component-kit.md#focusfallback)).
The second is a raw or foreign control whose markup never actually rendered `field.ElementId` as
its `id` attribute, which is why `FormidableField`'s `ForeignControl.razor` sample splats
`@attributes="field.InputAttributes"` onto its `<select>`: one splat carrying the id, the state
class, and `aria-invalid`, `aria-describedby` and `aria-required` whenever each applies (see
[Component kit](component-kit.md)). The third is an element that carries the id and will not take
focus: a disabled control, one inside a closed `<details>` or an `inert` subtree, or a container
given the id without the `tabindex="-1"` that makes a `<div>` or a `<fieldset>` focusable at all.
A `FormidableFieldAnchor`-only registration with no id on the control it anchors has nothing
for the focus service to find. [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)
closes that gap, giving its native `InputText` the field's id alongside the anchor;
[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) illustrates it instead,
holding the id back until a focus misses and supplying it from `FocusFallback` for the retry.

Because the id is the whole of the lookup, a field with no input of its own can be focused just
as well. Give any element the field's `FormidableFieldId.For(...)` id and a `tabindex="-1"` so
it can hold focus, and that element is where the summary entry lands. `FormidableForm` does this
for the model-level field itself, on the `<form>` element it renders — see
[Component kit](component-kit.md) — so the all-suppressed defensive gate's summary entry lands
there with no page wiring. A collection's rules fail against the list rather than against any one
control, so nothing renders that id automatically; the sample gives the container holding every
row the collection's id by hand. A container shows nothing when it takes focus, so the sample
stylesheet marks those landings with an outline, scoped to `:focus-visible`. The scope matters
because `tabindex="-1"` leaves an element focusable by mouse, and a plain `:focus` rule would paint the
whole container whenever a click landed on its padding. Activating a summary entry from the
keyboard carries focus-visible through to the programmatic focus, so the keyboard path keeps the
mark while a mouse click gets `scrollIntoView` alone.

### Pointer activation and a button that moves

Focus is not the only thing a form has to keep steady under an input device. A click is a press
and a release, and the browser fires one only when both landed on the same element — so a
button that moves out from under a still pointer between the two produces no click at all, and
the visitor's submit is silently discarded. Disclosing a message above the button is exactly what
moves it, which makes this the ordinary case on a form rather than an exotic one.

Keyboard activation is immune, because focus follows the element rather than a coordinate: Tab to
the button and press Enter or Space and the button is still the button whatever the layout does.
Pointer and touch are the whole of the gap, and Formidable closes it by default: a root installs a
guard that re-delivers the displaced click to the button it began on. `FormidableForm` renders the
element that guard scopes to, so it always has one; attach mode looks for one and reports it when
there is nothing to find. See [Component
kit](component-kit.md#the-click-a-disclosure-displaces) for the mechanism and
[`ClickRecovery`](options.md#clickrecovery) for the conditions, the attach-mode diagnostic and the
opt-out.

## Where this is demonstrated

- The class rule and its interaction with the `Pending` state — every sample using
  `FormidableInputText` shows it implicitly; [Async validation](async-validation.md)'s
  pending-UI section is the most direct look at `Pending` specifically.
- Renaming two of `FormidableCssClasses`' five class names to fit a UI library's own —
  [`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor), which points
  `Invalid`/`Valid` at Bootstrap's `is-invalid`/`is-valid` and lets Bootstrap's own stylesheet do
  the rest; `Warning`, `Info`, and `Pending` are left at their defaults there. Bootstrap has no
  advisory tier of its own to remap onto, so the page maps nothing for them rather than inventing
  one — harmless on that page, since its validator never raises a warning or an info.
- A consumer stylesheet keying off those same class names with CSS custom properties instead of
  fixed colours — [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor).
- The `FieldCssClassProvider` bridge and a native `InputBase` picking up the same classes —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), covered in
  [Component kit](component-kit.md).
- `aria-invalid`/`aria-describedby` on a hand-rolled control —
  [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor).
- `FormidableSummary`'s live regions and click-to-focus, including the virtualize limit —
  [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor).
- The field id on markup the page renders itself — a native input, a collection's container —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
  [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) and
  [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (the venue region input and the
  attendees group). The model-level gate id no longer needs a page's help —
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and `/workout` both show
  the all-suppressed gate landing on the `<form>` element `FormidableForm` renders it on.
