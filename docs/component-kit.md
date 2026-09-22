# The component kit

**You should already know:** the four components that make a working form
([Quickstart](quickstart.md)), and the first look at `FormidableField` and
`FormidableCollectionMessage` for fields and collections beyond the kit's typed inputs
([Fields and collections](fields-and-collections.md)).

Every Blazor form needs the same handful of things wired up: something to own the
`EditContext`, a state class on each input that tracks validity, `aria-invalid`/
`aria-describedby` kept current as the user types, a message list per field, a way to move
focus to whatever failed. Build all of that by hand for every form and the wiring becomes the
maintenance burden, not the validation rules. Reach for a component kit that also picks your
CSS, and the wiring comes with a redesign tax the day the form needs a different look.

Formidable's kit is headless: every piece renders unstyled markup, and the renderless pieces
render no markup of their own. Validation state is expressed purely as class names and ARIA
attributes for a consumer's own CSS to style (see
[CSS and accessibility](css-and-accessibility.md)). Nothing in it assumes a particular UI
library. `FormidableForm` and the kit's typed inputs — `FormidableInputText` foremost among them —
are the ready-made shapes, a convenient default. `FormidableInputText` in particular is a
convenience over the seam, not a contract of its own: a plain `<input>` with the registration,
ids, aria and state class already wired in, because a text box is every form's common case. The
renderless shapes exist specifically so any UI library, or plain HTML, can sit on top of the same
engine without being wrapped by it. This page documents all of them, introduced in the order a
growing form actually reaches for them.

## Need to know

Four names make a complete form, met together in [Quickstart](quickstart.md):
`FormidableForm` owns the `EditContext`, `FormidableInputText` renders one validated field,
`FormidableFieldMessage` shows that field's own issues, and `FormidableSummary` lists everything
the form currently has to say. `FormidableForm` is the one with a contract worth stating explicitly —
it resolves what it needs from either an argument or the DI container, and refuses to guess.
`FormidableValidator` resolves identically, because both hosts go through the same factory:

```csharp
    internal static FormidableEngine<TModel> Create<TModel>(
        TModel model,
        EditContext editContext,
        IServiceProvider services,
        IModelValidator<TModel>? validator,
        FormidableOptions? options,
        Func<Func<Task>, Task> renderDispatch)
        where TModel : class =>
        new(
            model,
            editContext,
            validator ?? ResolveValidator<TModel>(services),
            ResolveIntrospector(services),
            options ?? ResolveOptions(services),
            renderDispatch: renderDispatch,
            logger: ResolveLogger(services));
```

```csharp
    private static FormidableOptions ResolveOptions(IServiceProvider services) =>
        (FormidableOptions?)services.GetService(typeof(FormidableOptions)) ?? new FormidableOptions();
```

*Excerpt from `src/Formidable.Blazor/FormidableEngineFactory.cs`*

Three things get resolved, under one rule: what you passed wins.

- **The validator.** An explicit `Validator` parameter wins. Otherwise `IModelValidator<TModel>`
  comes from the container. What wins is the whole validator, capabilities included: the shipped
  FluentValidation adapter implements `IRuleInspectingValidator<TModel>` and
  `IRuleLevelValidator<TModel>` beside the validation seam, and a wrapper written against
  `IModelValidator<TModel>` alone presents neither. Derive a wrapper from
  `DelegatingModelValidator<TModel>` instead, which forwards all three:
  [wrap the validator](recipes.md#i-want-to-wrap-the-validator-without-losing-what-it-can-do).
- **The introspector.** `IModelIntrospector` comes from the container or throws. There is no
  parameter for it.
- **The options.** An `Options` parameter wins, then an app-wide default registered through
  [`AddFormidableBlazor(...)`](#addformidableblazor), then `new FormidableOptions()`.

A fourth thing is resolved too, on a simpler rule that has no parameter to win: the engine's
logger comes from `ILoggerFactory` when one is registered, or stays `null` otherwise. It carries
the suppressed-issue diagnostic described under
[`SuppressedIssueDiagnostic`](options.md#suppressedissuediagnostic), and an Information line
naming a validator that cannot report its own rules, which
[wrapping a validator](recipes.md#i-want-to-wrap-the-validator-without-losing-what-it-can-do)
explains.

The validator's two failures are worth reading in full, because between them they are how a first
form fails to start:

```csharp
        return resolved ?? throw new InvalidOperationException(
            $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered in the " +
            "container this render is resolving from — call services.AddFormidableBlazor() and " +
            "register the FluentValidation validator there. A two-project Blazor Web App has " +
            "one container per project, and a page that prerenders or runs on the server's " +
            "circuit resolves from the server's, so register there too.");
```

*Excerpt from `src/Formidable.Blazor/FormidableEngineFactory.cs`*

```csharp
    /// <summary>Names the missing validator registration and the two ways to make it.</summary>
    internal static string For(Type modelType)
    {
        var name = FriendlyTypeName.Of(modelType);
        return $"No FluentValidation validator for '{name}' is registered, so Formidable's " +
            $"IModelValidator<{name}> adapter cannot be constructed. Register one with " +
            $"services.AddScoped<IValidator<{name}>, {name}Validator>(), or register a whole " +
            "assembly's validators at once with services.AddValidatorsFromAssembly().";
    }
```

*Source: `src/Shared/MissingFluentValidatorMessage.cs`*

The first fires when the container this render is resolving from has no
`IModelValidator<TModel>`. A
single-project app has one container, so that means no `AddFormidableBlazor()` call anywhere; a
two-project Blazor Web App has one container per project, and a page that prerenders or runs on the
server's circuit resolves from the server's — [Hosting models](quickstart.md#hosting-models) has
the one page shape the server never builds. The second is the far commoner one: Formidable is
registered, so the open-generic adapter exists, but the FluentValidation validator it wraps does
not. That state makes the container throw while *building* the adapter rather than return null, so
it gets caught and renamed; the container's own exception is preserved as the inner one.
`Formidable.AspNetCore` reports the same state in the same words, from that one shared source file.
Both strings are Formidable's, which is what keeps them readable in a trimmed WebAssembly build.

None of the three resolutions is configurable beyond that — it's the one place the kit fails
loudly instead of quietly doing nothing, and it's worth knowing about before an exception is the
first time you meet it.

That's the one contract worth holding onto before the reference proper starts. What follows
introduces the rest of the kit in the order a growing form reaches for it: `FormidableForm` in
more depth, `FormidableInputText` and the kit's other typed inputs, the surfaces a form needs
once it has something to say — messages and the summary — and finally the seams for controls
Formidable doesn't wrap itself.

## `FormidableForm<TModel>`

The primary root component. It owns the `EditContext` — nothing else in a Formidable-based form
creates or replaces one — and renders a real `EditForm` underneath. So anything that already
expects standard Blazor forms interop (native `InputBase` descendants, `ValidationMessage`, a
`DataAnnotationsValidator` alongside it) keeps working:

```csharp
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Keys the cascade below on _context's own identity (the same idiom EditForm itself uses
        // for EditContext) — see the trailing comment for why this region exists.
        builder.OpenRegion(_context!.GetHashCode());
        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "IsFixed", true);
        builder.AddComponentParameter(2, "Value", _context);
        builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenComponent<EditForm>(0);
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _engine!.EditContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            // Rendered BEFORE the splat, so a consumer can splat it away (novalidate="@false").
            // The default is deliberate: without it, any native constraint attribute inside the
            // form — a splatted required/pattern/min/max, a type="email" — has the browser block
            // the submit and front its own bubble before OnSubmit ever fires, so the message the
            // visitor sees stops being FluentValidation's. novalidate switches off only that
            // interactive check: :invalid still matches, ValidityState is still computed, and
            // checkValidity()/reportValidity() still work when called.
            inner.AddAttribute(3, "novalidate", true);
            inner.AddMultipleAttributes(4, AdditionalAttributes!);
            // Rendered after the splat: id and tabindex win the duplicate-attribute race outright,
            // because the all-suppressed gate's summary entry addresses the form by this id (see
            // FormidableFieldId), and a consumer-supplied id or tabindex would break that the same
            // way a consumer-supplied input id would — see FormidableInputBase<TValue>'s identical
            // policy. aria-describedby, added in this same position, does not win outright — see
            // ComputeModelLevelAriaDescribedBy for why it merges instead.
            inner.AddAttribute(5, "id", _modelLevelFieldId);
            inner.AddAttribute(6, "tabindex", "-1");
            inner.AddAttribute(7, "aria-describedby", ComputeModelLevelAriaDescribedBy());
            inner.AddComponentParameter(8, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent?.Invoke(_context!) ?? (_ => { })));
            inner.CloseComponent();
        }));
        builder.CloseComponent();
        builder.CloseRegion();
        // IsFixed: a non-fixed CascadingValue re-supplies every subscriber's parameters from a
        // snapshot of the parent's PREVIOUS render on every re-render of this component, even
        // though FormidableFormContext is the same instance — that stale re-supply is what let a
        // kit input's own just-committed Value be overwritten with the value it held a moment
        // earlier (a caret jump in text, a wiped segment in a date input). Losing that
        // notification costs nothing an input needs: ongoing state (touched, validating, errors)
        // never travels through the cascade at all, fixed or not — it travels through
        // Engine.StateChanged, which every kit input subscribes to directly (see
        // FormContextBinding). The cascade's only remaining job is handing a descendant ITS OWN
        // reference to the context once, at mount. The region above turns a Model swap into
        // exactly that kind of mount for every descendant: it keys this cascade on _context's own
        // identity, so a swap destroys this component — not merely what renders below it — and a
        // fresh instance takes its place, whose subscribers are therefore all newly mounted and
        // read the swapped-in Value on their own first render, with no notification to miss.
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

The attributes `FormidableForm` sets on that `<form>` itself take three positions against the
splat rather than two: `id` and `tabindex` win it outright, `novalidate` loses it outright, and
`aria-describedby` merges with it.

The `<form>` element always carries the model-level field's id and `tabindex="-1"` — the same
`FormidableFieldId.For(...)` id every other field-owning element in the kit renders, computed
from the engine's own model-level field (the model paired with an empty path). That is the landing
spot the all-suppressed defensive gate's summary entry needs (see
[Progressive disclosure](disclosure.md) and
[CSS and accessibility](css-and-accessibility.md)); a page using `FormidableForm` never has to
render it by hand. A consumer-splatted `id` or `tabindex` on `FormidableForm` is ignored for the
same reason a consumer-splatted `id` on `FormidableInputText` is: the value has to stay
deterministic for the focus service to find it. `FormidableValidator<TModel>` renders no `<form>`
of its own — see its section below for the attach-mode equivalent.

The `<form>` also renders `novalidate`, by deliberate default. The browser's interactive
constraint validation answers a submit before Blazor hears about it: any native constraint
attribute inside the form (a splatted `required` or `pattern`, a `type="email"` on
`FormidableInputText`, a `min` or `max`) would block the submit at the element and front the
browser's own bubble, so the message the visitor saw would stop being FluentValidation's.
`novalidate` switches off exactly that check and nothing else. `:invalid` still matches,
`ValidityState` is still computed, and `checkValidity()`/`reportValidity()` still answer when
called — a stylesheet keying on `:invalid`, or a page asking the browser directly, sees what it
always saw. The attribute renders before the splat, which is the position a consumer wins:
splatting `novalidate="@false"` removes it and hands the submit back to the browser's native
constraint UI. That opt-out is the bool `@false` — the string `"false"` would render the
attribute, and a rendered `novalidate` is on whatever its value says.
`FormidableValidator` cannot make this call for a page: it renders no `<form>` and does not
reach the `EditForm` the page owns, so in attach mode the attribute is the page's own to write —
see [its section below](#formidablevalidatortmodel-attaching-to-an-existing-form) for where it
goes.

`aria-describedby` is the third position, and it is the only one that keeps both values. The
`<form>` points at the model-level message list's id — `FormidableFieldId.MessagesFor` of the id
above — so a `FormidableModelMessage` describes the form without the page wiring anything. A
consumer who splats an `aria-describedby` of their own neither loses it nor replaces the computed
one: the two are joined, splatted ids first and the computed id appended, which is the
consumer-first shape a kit input applies to its own `aria-describedby`. Splatted first because
they are the only ids describing anything while `FormidableModelMessage` is absent or empty, and
appended rather than reshuffled so a page's own hint stays where the page put it. Attach
mode writes this attribute by hand alongside the gate id — see
[its section below](#formidablevalidatortmodel-attaching-to-an-existing-form).

`ChildContent` is a typed fragment, `RenderFragment<FormidableFormContext>` — the shape
`EditForm` gives its own body — so form markup reads the cascaded context as `context` without
capturing the component with `@ref`: a control the page draws itself marks its own field touched
on blur with `@onblur="() => context.Engine.MarkTouched(field)"`, inline. Three notes travel with
that. What the context reaches is the engine's members, plus one move `FormidableForm` makes that
the engine cannot. `context.FocusFirstErrorAsync()` is on the context because the parameters
governing that move, `PrepareFocus` and `FocusFallback`, are the form's, and no focus service is
reachable from the engine at all. That is what lets a shared component nested inside the form (the
announcement dialog a design system builds once and every form reuses) finish the
[dialog sequence](#asking-for-the-first-error-move) itself, rather than having the page thread a
callback down to it. Every other member declared on `FormidableForm` is out of reach that way, and
`ResetAsync` is the one that shows where the line falls rather than being an exception to it:
resetting rebuilds the engine, so a context asked to do it would invalidate the very instance it
was asked on, where moving focus invalidates nothing. That is why a reset button is the case that
does want an `@ref` to the form. Where the engine and the component both declare a member, the
engine's is what the context reaches, so `context.Engine.ApplyServerIssues(...)` is the quiet
background apply rather than the form's own overload, which also moves focus to the first error.

An inline *read* of engine state refreshes when the form itself re-renders (a submit is one
cause), not on every validation pass — ongoing state travels through `Engine.StateChanged`, which
observing components subscribe to individually — so a live spinner or a disabled submit button
wants something subscribed to that event to re-render the markup holding the read. Where the read
sits on the page, that something is the page: `/field-state` reads `IsFormValid` inline for its
readout and its disabled Submit and subscribes from its code-behind, as `/async` does for the
`IsValidating` line under its own button. Where a component owns the read, it subscribes for
itself — what the kit's own components do, and what a [hand-rolled
summary](recipes.md#i-want-my-own-summary-markup) has to do.

And nesting another typed fragment that also leaves its parameter name implicit (a
`FormidableField`, a `Virtualize`) makes the Razor compiler ask for a `Context="..."` on one of
the two: both declare the implicit `context` name, so the rename is owed whether or not either
body ever reads it. It is a compile-time rename, the same one `EditForm` has always charged, and
either fragment can take it. The form's own can:
`<FormidableForm Model="_order" Context="formidable">` names the form's body and leaves the
nested fragment on `context`. The one sample that names the form's body is
[`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor), whose line under the Submit
button reads `formidable.Engine.IsValidating` inline; everywhere else the rename, where one is
owed at all, goes on the nested fragment.

Swapping the `Model` parameter to a different instance — a draft load, a "start over" reset —
rebuilds the `EditContext` and engine; a component consuming `FormidableForm` never manages that
lifecycle itself. (`ResetAsync` below is the same rebuild asked for directly, rather than through a
parameter change.)

```csharp
    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        VerifyInteractiveRenderMode();

        if (Model is null)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableForm<TModel>)} requires a Model parameter (none was supplied) — set Model " +
                "to the object being edited, e.g. <FormidableForm Model=\"_order\">.");
        }

        if (!ReferenceEquals(_boundModel, Model))
        {
            RebuildEngine(Model);
        }
        else
        {
            FormidableEngineFactory.VerifyOptionsUnchanged(
                nameof(FormidableForm<TModel>),
                _boundOptions,
                Options,
                "swap the Model parameter alongside Options to rebuild the engine");
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

The Model-swap branch also caches the model-level field's rendered id (`_modelLevelFieldId`)
alongside the engine it is derived from, rather than recomputing it on every render — the same
idea `FormidableInputBase<TValue>` applies to its own `ElementId` below.

The same method carries three guards worth knowing about. `VerifyInteractiveRenderMode()` runs
first: a page rendered statically with no interactivity coming can render a form but can never
submit one, so the form says that in its own words instead of letting the platform answer the
first submit with a 400 about `FormName`. A render mode assigned to the page or component is what
tells that dead end apart from the static prerender pass of a component that is about to become
interactive, which is left alone. An omitted `Model` throws a message that names the component
and the parameter, rather than the bare `ArgumentNullException` a plain null guard would
produce. And since the engine reads `Options` once, at construction, handing the
parameter a *different* `FormidableOptions` instance without also swapping `Model` throws as well
— see [Engine options](options.md#formidableoptions-is-read-once) for the shape that keeps a form
out of that state. Swapping both together is the supported path, and the one the
`/custom-profiles` sample takes.

`SubmitAsync()` is both the handler wired to the rendered `EditForm`'s `OnSubmit` and a public
method a consumer can call directly — a toolbar "Submit" button outside the form element, a
keyboard shortcut. It runs the submit pipeline and routes to `OnValidSubmit` or
`OnInvalidSubmit`:

```csharp
    /// <summary>
    /// Runs the submit pipeline programmatically. Call from the renderer's synchronization
    /// context (a Blazor event handler or <c>InvokeAsync</c>) — it triggers renders. If
    /// <see cref="ResetAsync(TModel?)"/> rebuilds the engine while this call is still awaiting the
    /// pipeline, the engine that started is gone by the time the verdict lands: neither
    /// <see cref="OnValidSubmit"/> nor <see cref="OnInvalidSubmit"/> fires, focus is not moved, and
    /// no render is triggered — a dead engine's verdict, from a submit the reset already abandoned,
    /// must not surface as if it were current. That includes the return value: cancelling the
    /// abandoned pass's token is what usually stops it short, but a validator that does not honour
    /// the token can still run to completion, so a blocked <see cref="SubmitOutcome"/> carrying
    /// nothing is returned instead of whatever that pass actually decided.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var outcome = await RootSubmit.RunAsync(RequireEngine(), () => _engine);
        if (outcome is null)
        {
            return RootSubmit.Superseded;
        }

        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync(outcome);
        }
        else
        {
            // A fresh context per blocked submit, which is the whole of what keeps suppression
            // from latching: the handler's answer lives on the object it was handed, and the next
            // submit hands it a different one.
            var invalidSubmit = new FormidableInvalidSubmitContext(outcome);
            await OnInvalidSubmit.InvokeAsync(invalidSubmit);
            if (FocusFirstErrorOnInvalidSubmit && !invalidSubmit.FirstErrorFocusSuppressed)
            {
                await FocusFirstErrorAsync();
            }
        }

        StateHasChanged();
        return outcome;
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

Both branches hand their handler the outcome. `OnValidSubmit` is an
`EventCallback<SubmitOutcome>` and hands it over directly: a passing submit can
still carry advisories, and a handler that wants them shouldn't have to reach through `Engine` to
find them. `OnInvalidSubmit` is an `EventCallback<FormidableInvalidSubmitContext>` and wraps it,
because a blocked submit is the one a handler may want to answer itself:
`context.Outcome` is the same value, and `context.SuppressFirstErrorFocus()` is how the handler
says it has taken over (see [`SuppressFirstErrorFocus`](#suppressing-the-automatic-focus) below).
A parameterless handler still binds to either — Blazor's own `EventCallback<TValue>` conversion
accepts an `Action` or a `Func<TResult>` wherever a typed callback is declared, the same way it does
for `EditForm.OnValidSubmit` — which is why `OnValidSubmit="HandleValid"` over a `void
HandleValid()` is still the shape every sample page uses. Take the parameter when the outcome is
what you're after: `CanProceed`, the full `Report`, and `VisibleErrorSummary` for a dialog (see
[Severity](severity.md)).

The engine itself is exposed as a public property: `Engine => _engine`, typed as the non-generic
`IFormidableEngine`. `FormidableForm` also forwards its two overloads directly
(`_form!.ApplyServerIssues(issues)`; see [Server integration](server-integration.md)), and a page
reads `IsValidating`, `HasSubmitted` or `IsFormValid` off `Engine` without going through a field.
The last of those needs [`TrackFormValidity`](options.md#trackformvalidity) turned on to mean
anything; the other two are always current.

A blocked submit also moves keyboard focus, by default: `FocusFirstErrorOnInvalidSubmit`
(default `true`) resolves the first error in `Engine.GetVisibleIssues()` and focuses its
element via `IFormidableFocusService` — the same service `FormidableSummary`'s click-to-focus
uses, so a form with no summary rendered still lands a visitor on the problem instead of leaving
focus wherever the submit button was. The first error, not simply the first entry:
[issue order follows the page](#the-order-entries-appear-in), so a field above the failing one may
carry nothing worse than a warning, and landing there would bury the reason the submit blocked —
and disagree with `FormidableSummary`, which groups by severity and leads with the error
regardless. It falls back to the first visible
issue when there is no error to find at all. A blocked submit reaches that state by being
superseded before its own verdict landed — by a second submit, or by the pass
`DiscloseLoadedValuesAsync` runs, the two things a caller starts and awaits: it reports blocked
without writing a verdict, leaving whatever preceded it on screen. Every other way a submit blocks
writes an error-severity issue (the all-suppressed gate's explanation among them), so the fallback
has nothing else to catch. A validator fault is error-severity too, when a live or refresh pass is
the one reporting it: it never shows up from a submit itself, since `RunPassAsync` catches the
exception only for those two kinds of pass, so a fault during submit propagates to the caller
instead of leaving a blocked verdict behind.
An app that never registered `IFormidableFocusService` (only `AddFormidable()`, not
`AddFormidableBlazor()`) gets silence rather than a resolution failure. A focus miss is different:
when the field does not take focus — nothing carries its id yet, as for a row scrolled out of a
`Virtualize` window, or something does carry it and will not accept it —
the form retries it once through its own `FocusFallback` parameter, identical in name and delegate
shape to [`FormidableSummary.FocusFallback`](#focusfallback) below, so a page wiring both typically
passes the same callback to each. With no `FocusFallback` wired, the miss does not fall silent the
way the summary's click-to-focus does. A blocked submit has nowhere else for the visitor to land,
so it reports a diagnostic instead: a `Trace`-output line, plus a `LogWarning` naming
`FocusFallback` by parameter name when the host resolved an `ILoggerFactory` — the same dual
channel [`SuppressedIssueDiagnostic`](options.md#suppressedissuediagnostic) writes to.
Which of the two seams a page needs turns on one question: can the element take focus at the
moment the move is made? A field under a CSS overlay can, so the move lands and reports success,
and the caret ends up in a box the visitor cannot see. Only
[`PrepareFocus`](#preparefocus) prevents that, because it runs before the attempt. A field inside a
collapsed section cannot, so that move does miss and the fallback gets its one retry — but
`PrepareFocus` is still the better seam for it, clearing the way before anything is tried rather
than after something has already failed.
Set `FocusFirstErrorOnInvalidSubmit="false"` to turn the automatic move off for every submit, and
[ask for it yourself](#asking-for-the-first-error-move) when the page is ready.

The same parameter governs a server's verdict. A rejected round trip is a blocked submit that
arrived late, so `ApplyServerIssues` on the form focuses the same first-error target when the
payload it applies carries an error — not necessarily the issue just applied, since the page may
already be showing one above it. A payload with no error in it moves nothing: an accepted
resubmission rejected nothing, whatever an earlier one left on screen. Reach through `Engine`
(`_form!.Engine!.ApplyServerIssues(issues)`) when an apply has to stay quiet, such as a
background poll refreshing a verdict nobody just asked for — the engine-level method never moves
focus.

**Sample:** [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor) — a toggle
above the form flips the parameter, so a submit's automatic focus and the opt-out sit side by
side.

### Asking for the first-error move

`FocusFirstErrorAsync()` makes the move on demand. It is the same move, not a second one: the
submit path calls this method, so the field it lands on, the `PrepareFocus` awaited ahead of the
attempt and the `FocusFallback` that recovers a miss are the same code with a different caller.
`FocusFirstErrorOnInvalidSubmit` does not gate it — that parameter decides what a blocked submit
does unasked, and this call is the page deciding. `FormidableValidator` carries the identical
method for a page in [attach mode](#formidablevalidatortmodel-attaching-to-an-existing-form).

```razor
<FormidableForm @ref="_form" Model="_request" OnInvalidSubmit="AnnounceAsync">
    @* the form's own fields and buttons *@
</FormidableForm>
```

```csharp
    // Wired to your dialog's own Close button and its Escape handler: the ways out that name no
    // field, where nothing else is going to move focus to one.
    private async Task CloseAnnouncementAsync()
    {
        await _announcement!.CloseAsync();
        await _form!.FocusFirstErrorAsync();
    }
```

It returns `true` when an element took focus and `false` when nothing did — no visible issue to
land on, no `IFormidableFocusService` registered, or an element the focus miss and its fallback
could not reach between them. That is a report on the move rather than on the form: a form with no
issue on screen and a form whose one error is out of reach both answer `false`. Read
`Engine.GetVisibleIssues()` to tell those apart; ignore the answer if there is nowhere else to
send the visitor, since a `false` means the call moved nothing.

Call it from the renderer's synchronization context, as with `SubmitAsync`, and after the form's
first render — before that there is no engine and the call throws.

A component nested *inside* the form asks for the same move through the cascaded context, with no
`@ref` to reach for: `context.FocusFirstErrorAsync()`. It calls the root's method, so the field it
lands on, `PrepareFocus` and `FocusFallback` all come with it, and the answer means what it means
above. That is what lets a shared announcement dialog — one a design system builds once and every
form drops in — finish the sequence itself instead of taking a callback from each page that hosts
it. A `FormidableFormContext` built through its public constructor (the shape
[Testing](testing.md) points at) has no root behind it, so it moves nothing and answers `false`.

### Suppressing the automatic focus

A dialog that announces a blocked submit is the case `FocusFirstErrorOnInvalidSubmit` alone
handles badly. The form's own move runs immediately after `OnInvalidSubmit` returns, inside the
same submit call, so the caret lands in a box the overlay is covering. Turning the parameter off
at the top of the markup fixes that submit and every other one, including the submits where no
dialog opens.

The handler declares it instead, at the moment it opens the dialog:

```csharp
    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        context.SuppressFirstErrorFocus();
        await _announcement!.OpenAsync();
    }
```

That is a statement about this submit. The form builds a fresh `FormidableInvalidSubmitContext`
for every block, so nothing latches: a handler that suppresses one block and stays quiet on the
next gets the automatic move back for that second one. `FirstErrorFocusSuppressed` reads what has
been said so far, so a handler that delegates to a helper can see whether that helper already
covered the form.

It suppresses; it does not request. A form already carrying
`FocusFirstErrorOnInvalidSubmit="false"` was not going to move focus on a blocked submit anyway,
and the way to ask for a move is
[`FocusFirstErrorAsync()`](#asking-for-the-first-error-move) — which is what the
sequence ends with: suppress, open the dialog, close it, focus.

Why the form cannot decide this for itself is worth stating, because "a handler exists" looks
like it should be enough. It isn't: what breaks the move is the handler having *covered the form*,
and a handler that logs telemetry or scrolls a banner into view has not. There is no dialog mode
to key off, so forbidding the combination would block the uses the library cannot tell apart from
it. The handler knows, so the handler says.

**Sample:** [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) — a toggle
skips the suppressing call, so the failure it prevents is visible rather than described.

### Returning the form to pristine

`ResetAsync` is the named verb for "start over" — the clean slate a `Model` swap produces, without
needing a different object to get it:

```csharp
await _form!.ResetAsync();                // same instance, back to pristine
await _form!.ResetAsync(new Order());     // a different instance — needs @bind-Model
```

Called with nothing, it rebuilds the engine and the `EditContext` over the model instance already
bound. Touched and modified state, the message store, the advisory buckets and `HasSubmitted` all
clear, and any pending refresh is cancelled — none of it survives the engine it belonged to. An
in-flight `SubmitAsync` is abandoned with that engine too, exactly as its own remarks above
describe: the verdict of a submit the reset already walked away from never surfaces.

Called with a model, it swaps to that instance — and that swap needs the parent's cooperation,
which the form insists on rather than hopes for. `Model` is a `[Parameter]`, and Blazor re-supplies
a component's parameters from whatever the parent still holds on every one of the *parent's* own
renders, not just this component's. So a swap the form makes to its own copy is silently reverted
the next time anything up there re-renders, unless the parent's field moved too. `ResetAsync`
invokes `ModelChanged` to make that happen, and `@bind-Model` is how a page binds it:

```razor
<FormidableForm @bind-Model="_order" @ref="_form" OnValidSubmit="HandleValid">
```

Supplying a new model with no `ModelChanged` delegate bound throws instead, naming that fix — a
targeted exception beats a swap that appears to work until an unrelated re-render undoes it. Both
shapes trigger renders, so call them from the renderer's synchronization context: a Blazor event
handler, or `InvokeAsync`.

**Sample:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) — a Reset button
calling the no-argument shape, beside the draft and submit buttons whose state it clears.

### Saying what loaded values have earned

A form filled from somewhere other than this visitor's typing — a saved draft, a prefilled
application, a record opened for editing — looks pristine however good or bad its contents are,
because every state class and every live message waits on a committed change. Writing model
properties notifies nothing, so the values appear in the boxes and the form goes on saying nothing
about them. `DiscloseLoadedValuesAsync` is the one call that answers for what is already there:

```csharp
        _proposal.Title = "Progressive disclosure in practice";
        _proposal.ContactEmail = "ada.lovelace";
        _proposal.Summary = string.Empty;

        await _form!.DiscloseLoadedValuesAsync();
```

*Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor.cs`*

It validates the whole model under `SubmitProfile` and then decides field by field, on whether the
field **holds a value**. Three outcomes, because a good value, a wrong value and a field nobody
has reached are three different situations:

| The field | What it is marked | What you see |
|---|---|---|
| Holds a value no rule fails with an error | Touched and engaged | The valid class, or whichever advisory tier its warnings and infos earn |
| Holds a value the rules do fail | Touched and engaged | Its message, inline and in the summary |
| Holds nothing | Neither | Nothing: unstyled and silent, with any required mark it carries still standing |

It engages rather than merely marking touched, and that is the whole of why the middle row works.
Touched alone paints the good fields green and leaves a bad saved value silent among them, and
silence in a row of green reads as "not filled in yet" rather than "this one is wrong".
Engagement then behaves in the ordinary way: the fields it names go on being answered by every
later live pass, exactly as a field the visitor had typed into would be, until they leave the page.

"Holds a value" is the negation of what FluentValidation's own `NotEmpty()` rejects, read against
the type the member is **declared** as. Holding nothing is null, a blank or whitespace-only
string, a collection with no elements, or the default of a non-nullable value type. A saved
`false` in a `bool?` is an answer and earns its class; an untouched `bool` is not, and the same
separates `int` from `int?` and `DateTime` from `DateTime?`. The ambiguity that leaves is exactly
the non-nullable value types, and it errs towards silence. Model an optional value as `T?` and a
load reads it exactly; model it as `T` and its default reads as "not filled in".

The two outcomes need different things, which decides what a validator that cannot be inspected
can still do. Disclosing a wrong value needs only the model, so it happens whatever the validator
is, a hand-rolled `IModelValidator<TModel>` included. Vouching for a good one needs the validator's
own list of the fields it has rules for, because nothing else separates a field whose rules all
passed from a field no rule mentions — so a validator with no inspection capability turns a
wrong value red and confirms nothing. That list is the validator's declared shape, child
validators and `Include`d rules with it, so a field inside a collection row is confirmed and
disclosed exactly as a top-level field is.

Where a value cannot be read at all, nothing is claimed: a nested path whose owner is null, a
model-level failure, which names no member. Those fail in the safe direction, leaving the field
unstyled rather than painting it red. One residual is worth knowing before wiring this to a model
whose constructor seeds placeholders: a seeded value is a value. Seed one your own rules reject and
the form rejects it the moment the values load. Seeding the default instead leaves the field silent
until someone reaches it.

The cost is that pass — one whole-model validation, async rules included — plus the live
pass that discloses what it found. No option has to be turned on for the valid class to appear:
green is a promise about submit, and this is a submit-profile answer for the whole model, so the
coverage that promise rests on is earned by the call rather than borrowed from
[`TrackFormValidity`](options.md#trackformvalidity). No field reports `IsValidating` while that
whole-model pass runs and none wears the pending class, since nobody asked for it; the
engine-level flag is true throughout, for a page-level spinner to read. Unlike a blocked submit
it moves no focus: nothing was refused, and a page that has just loaded is not one to take the
visitor somewhere in. Call it from the renderer's synchronization context. A form that never
calls it is unaffected in every respect.

**Sample:** [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor) — three saved
values and three different answers, side by side.

## `FormidableInputText` and `FormidableInputBase<TValue>`

`FormidableInputBase<TValue>` is the base every validated input component builds on — the kit's
own and yours. It is public for that second reason: when the kit doesn't ship the control you
want, deriving from it is the supported way to get one (see [Deriving your own
input](#deriving-your-own-input) below). It provides five things, and a concrete input reaches all
five with two calls: `AddCommonAttributes` right after opening its element, for the attributes
every validated input shares, and `AddValueBinding` right before closing it, for whichever
value-commit attribute(s) `UpdateOn` calls for:

**Registration.** Binding to the cascaded context is the one lifecycle every component in the kit
shares, so it lives one level further down, on `FormidableComponentBase`: bind when parameters are
set, rebind when the context instance is replaced, release on disposal.

```csharp
    protected override void OnParametersSet()
    {
        if (_binding.IsBound(Context))
        {
            VerifyRowKey();
            return;
        }

        _binding.Update(
            Context,
            GetType(),
            register: Register,
            stateChanged: ObservesEngineState ? OnEngineStateChanged : null);

        _verifyRowKeys = Context!.Engine.Options.VerifyRowKeys;
        _registeredField = _verifyRowKeys ? ResolveField() : default;
    }
```

*Source: `src/Formidable.Blazor/FormidableComponentBase.cs`*

The leading `IsBound` check is a fast exit for the common case — a parent re-render with the same
cascaded context — so a steady-state render returns before it touches the registration or the
subscription at all. `VerifyRowKey` on that path is the development-time row-key check, and unless
`FormidableOptions.VerifyRowKeys` asked for it, it tests one bool and returns.
`FormidableComponentBase` is public only because a public component cannot
inherit a less accessible base; its constructor is not, and it is not an extension point —
`FormidableInputBase` below and `FormidableField` further down are still the two ways to bring a
control of your own to the engine. Registering a field is a narrower job than that, and it has
its own supported route: see
[`FormidableFieldAnchor`](#formidablefieldanchortvalue) for the component that does only that,
and for what calling the registry yourself takes on.

What an input contributes to that lifecycle is its `Register`: it resolves the field to a
`FieldIdentifier`, computes the ids that address it, and registers it with the cascaded context's
`FieldRegistry` — which is what makes the field's issues visible to progressive disclosure while
the component stays mounted (see [Disclosure](disclosure.md)):

```csharp
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
        // A commit made against the outgoing context is not delivered to its successor.
        _notificationPending = false;
        Field = ResolveField();
        ElementId = FormidableFieldId.For(Field);
        MessagesElementId = FormidableFieldId.MessagesFor(ElementId);
        return context.Registry.Register(Field, KeepRegistered);
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

`Register` runs only when the binding targets a new context instance — the first render, and every
rebind after it — which is why the field resolves there rather than per render: a rebind is exactly
when that resolution can have changed. An input's `Register` is sealed: which field a control
speaks for is not one of the things deriving from it is meant to change. `ObservesEngineState`, the
other name in the snippet above, is the base's one opt-out from the engine subscription — it exists
for a component that renders nothing at all, and [`FormidableFieldAnchor`](#formidablefieldanchortvalue)
below is the component that takes it.

Either spelling names that field. `@bind-Value="_order.Description"` fills in `ValueExpression`,
which the Razor compiler supplies for every `@bind-Value` — the same `Value`/`ValueChanged`/
`ValueExpression` triple native inputs take — so ordinary markup states the field once and
`<FormidableInputText @bind-Value="_order.Description" />` is a complete input. `For` is the
explicit spelling: pass it when the input has no `@bind-Value` at all, or to point registration,
messages and focus at a field other than the one the value binds. `For` wins when both are
present, and it wins silently — bind-only markup is the idiom precisely because two spellings of
one field can disagree. With neither, the input throws on its first render, naming itself and both
spellings.

**Ids.** The same registration computes `ElementId` — the deterministic id every input, message
list, and the focus service address the field by (see
[CSS and accessibility](css-and-accessibility.md)) — and `MessagesElementId` beside it, the
`aria-describedby` target `FormidableFieldId.MessagesFor` owns. Both are computed once at
registration rather than per render, because both change only when the field does.

**CSS.** The computed state class merges with any consumer-splatted `class` rather than
replacing it — `CssClass` is the protected property, and one computation answers for it and
for the shared call below:

```csharp
    private string ComputeCssClass(FieldState state) =>
        FormidableCss.CombineClassNames(AdditionalAttributes, FormidableCss.Compute(state, Context!.Engine.Options.CssClasses));
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

```csharp
    internal static string CombineSplatted(IReadOnlyDictionary<string, object>? attributes, string attributeName, string computed)
    {
        if (attributes is null || !attributes.TryGetValue(attributeName, out var splatted))
        {
            return computed;
        }

        var splattedValue = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(splattedValue))
        {
            return computed;
        }

        return computed.Length == 0 ? splattedValue : $"{splattedValue} {computed}";
    }
```

*Source: `src/Formidable.Blazor/FormidableCss.cs`* — `CombineClassNames` is this method's `class`
case; the merge lives on `FormidableCss` because it is not the inputs' alone — the message
components, `FormidableSummary`'s wrapper, and the `aria-describedby` merges on the `<form>`
element and on a kit input all answer a consumer's splatted value through this one
implementation.

A consumer writing `class="form-control"` on a `FormidableInputText` keeps that class and still
gets whichever state class applies — `formidable-invalid`, `formidable-warning`,
`formidable-info`, or `formidable-valid`, plus `formidable-pending` while a pass is in flight —
appended alongside it; the two are merged, never one replacing the other.

**Aria.** `aria-invalid` appears while the field has error-severity issues, and `aria-describedby`
— pointing at the message list's id — while it has any issues at all; a clean field renders
neither. A consumer-splatted `aria-describedby` (a persistent hint, say) merges rather than
loses: while issues exist the rendered value is the splatted ids first, then the messages id
appended, and while none exist the splat stands alone. Appending is the order that keeps the
hint's association stable — the messages id joins the end of the announced sequence when an
issue arrives, instead of reshuffling the hint at exactly the moment the visitor needs full
context. `aria-required` appears while the submit profile's rules demand a value for the field,
which is a fact about the rules rather than about the current value, so it is there from the first
render and stays through every state the field passes.

**Rendering them.** `AddCommonAttributes` is the first of the two calls: it renders the splat,
the id, the class and the aria attributes, and the order it renders them in is what the kit's two
consumer-facing guarantees rest on:

```csharp
    protected void AddCommonAttributes(RenderTreeBuilder builder, int sequence)
    {
        var state = State;
        var issues = Context!.Engine.GetIssues(Field);

        builder.AddMultipleAttributes(sequence, AdditionalAttributes!);
        builder.AddAttribute(sequence + 1, "id", ElementId);
        builder.AddAttribute(sequence + 2, "class", ComputeCssClass(state));

        // The aria attributes share one sequence number: attribute frames diff by name rather than
        // by sequence, and sharing it keeps this call's budget at four numbers for a control
        // numbering its own attributes around it.
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", ComputeAriaDescribedBy());
        }

        if (Context.Engine.GetFieldRequirement(Field) == FieldRequirement.Required)
        {
            builder.AddAttribute(sequence + 3, "aria-required", "true");
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

`AdditionalAttributes` enters the render tree first and the computed values after, so the computed
values win the duplicate-attribute race (Blazor applies last-write-wins): that is what merges a
consumer's `class` with the state class, merges a consumer's `aria-describedby` with the
messages id (the `ComputeAriaDescribedBy()` call in the excerpt builds that value — splatted ids
first, messages id appended), and drops a consumer's `id` in favour of the
deterministic one. Because the order lives in this one call, a derived control gets it by calling
rather than by transcribing. The call consumes four sequence numbers — `sequence` through
`sequence + 3` — so the control's own attributes start at `sequence + 4`. It also reads the field's
state and issues once each per render, and answers the class, `aria-invalid` and `aria-describedby`
from that one read; `aria-required` is a separate ask, of a cached answer.

**Value binding.** `AddValueBinding` is the call a concrete input's `BuildRenderTree` makes,
immediately before closing its element, to wire the attribute(s) that commit a value change —
honouring `UpdateOn` for every mode the enum has, present and future, rather than each input
re-deciding which DOM event to bind. There are three overloads, differing in how the DOM's text
becomes the field's value, and one implementation underneath all three:

```csharp
    private void AddCommitBinding<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TBound>(
        RenderTreeBuilder builder,
        int sequence,
        TBound current,
        Func<TBound, Task<bool>> commitAsync,
        bool inputEventAvailable)
    {
        // OnBlur is the one mode that splits commit from notify, so it is the one mode where the
        // commit's report earns nothing immediately and blur has a notification to deliver.
        var deferNotification = UpdateOn == InputUpdateMode.OnBlur;

        builder.AddAttribute(
            sequence,
            inputEventAvailable && UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TBound>(this, value => CommitThenNotifyAsync(value), current));

        // Marks the attribute just added, so it has to follow that AddAttribute and precede the
        // blur binding below.
        builder.SetUpdatesAttributeName("value");

        if (deferNotification || SyncsDomValueOnBlur)
        {
            AddBlurBinding(builder, sequence + 1);
        }

        async Task CommitThenNotifyAsync(TBound value)
        {
            var committed = await commitAsync(value);
            if (committed && !deferNotification)
            {
                NotifyChanged();
            }
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

Each overload hands that method a commit step: something that commits whatever the DOM sent and
reports whether it committed anything. What `UpdateOn` decides at render time is then settled in
the one place — which event carries the commit, whether the report earns a notification at once or
leaves one for `blur` to deliver, and whether `blur` is bound at all. Where the three overloads
agree they agree by running the same code; where one differs (the `<select>` coercion below), the
difference is an argument rather than a second copy.

The `SyncsDomValueOnBlur` half of that blur condition is the number and date inputs' opt-in: those
two controls bind `blur` in every mode, because their native elements can display text they report
as empty and only a blur-time write can reconcile the box with the model — the mechanism their own
sections below walk through.

Under `OnChange` (default) and `OnInput`, a single event both commits the value and starts
validation: the commit step runs, and the notification follows as soon as it reports a commit.
Those are the two steps `SetCurrentValueAsync` performs in one call, which is what a control
driving a commit from a handler of its own reaches for — it assigns `Value`, invokes
`ValueChanged`, marks the field touched, and notifies the `EditContext` so the engine's live
validation pass runs:

```csharp
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        await CommitValueAsync(value);
        NotifyChanged();
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

Under `OnBlur`, `AddValueBinding` calls the same two steps apart instead: `CommitValueAsync` alone
on `change` (assigns `Value`, invokes `ValueChanged`, arms a pending notification — no touch, no
notify yet), then `NotifyChanged` on `blur`, only while a commit has left a notification pending:
however many commits accumulate before the blur, it delivers exactly one, and a blur with none
pending delivers none. (`NotifyChanged` marks the field touched and notifies the `EditContext` —
the same two things `FormidableFieldContext.NotifyChanged` does for a foreign control with no
base class to call it from; see [the foreign-control
pattern](#the-foreign-control-pattern) below.) Neither half is
markup a derived control writes by hand: `AddValueBinding` is the one call that changes if a
control ever wants different `UpdateOn` behaviour, so a mode the control doesn't specifically know
about still gets a correct binding instead of silently falling back to `onchange`.

`FormidableInputText` is the reference implementation — a plain `<input>` wired through all five
extras, and a working example of how little markup the base class leaves to write:

```csharp
public sealed class FormidableInputText : FormidableInputBase<string?>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
```

*Source: `src/Formidable.Blazor/FormidableInputText.cs`*

The whole body is the element, the base's two calls, and the value between them. Those two calls
carry the ordering — `AdditionalAttributes` splats inside `AddCommonAttributes`, and every value
the component computes, `AddValueBinding`'s own event handlers included, is added after, so each
wins the duplicate-attribute race (Blazor applies last-write-wins). That's also why a
consumer-supplied `id` is silently ignored rather than merged: the rendered id must always be the
deterministic `FormidableFieldId`, because the message list's `aria-describedby` target and
`IFormidableFocusService` both address the field by it. The
`onblur` that `UpdateOn="OnBlur"` adds is the one handler that chains instead of winning: a
consumer's own `@onblur` runs first and is awaited, then any notification a commit left pending
is delivered. Wanting the
`blur` event for validation is not a reason to take it away from the page that also wants it.
If markup needs to label the input without relying on implicit wrapping, address it by
the context's id instead of assuming a consumer id sticks:

```razor
    <div class="field"><label>Name <FormidableInputText @bind-Value="_contact.Name" /></label>
        <FormidableFieldMessage For="() => _contact.Name" /></div>
```

*Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor`*

— label-wrapping is what every sample using `FormidableInputText` does, since the label needs no
explicit `for` when it wraps the control. `FormidableField`'s renderless template is the other
option, for markup that isn't wrapping — [the foreign-control pattern](#the-foreign-control-pattern)
below uses `<label for="@field.ElementId">` because the control it labels isn't a Formidable
component at all.

### The click a disclosure displaces

The three update modes decide when a value commits, and a commit is what discloses. On a form,
that has a consequence with nothing to do with typing: pressing the submit button blurs the field
the visitor was in, and under `OnChange` — the default — that blur is the commit. The commit
discloses whatever the value now fails, the message is inserted above the button, and the button
leaves the pointer between the press and the release.

The browser then does the right thing for the wrong situation. A click fires only when the press
and the release share a target; otherwise it dispatches on their nearest common ancestor, which
for a submit button is usually the `<form>`, where nothing is listening. That rule is what makes
dragging off a button cancel it, and it cannot tell a pointer that moved off a still button from a
button that moved out from under a still pointer. The result is a submit the visitor asked for and
nothing received. Keyboard activation is immune, since focus follows the element rather than a
coordinate; touch is affected exactly as the mouse is.

Formidable recovers it. A root installs a guard by default and re-delivers the click to the
button it began on — see [`ClickRecovery`](options.md#clickrecovery) for the three conditions
that have to hold, what the recovered click is, how a root that cannot scope a guard says so, and
how to turn it off. Nothing in a page's markup asks for it under `FormidableForm`, which renders
the `<form>` the guard scopes to.

Two things a page can do instead, both of which remove the shift rather than recovering from it:

- **`UpdateOn="InputUpdateMode.OnInput"`** on the fields above the button. Disclosure then happens
  as the visitor types, so by the time the button is pressed the message is already on screen and
  nothing moves. It costs a validation pass per keystroke, which is the trade
  [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) and
  [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor) make.
- **Reserve the space the messages will take**, so inserting one moves nothing. This is the
  layout-side answer, and it takes real height: a persistent wrapper is not enough on its own,
  because an empty element is zero-height. `FormidableFieldMessage` renders its list element
  always, which is what gives a stylesheet something stable to give a `min-height` to. The
  sample's own `.summary-slot` shows the limit of the weaker version: the summary's wrapper and
  regions persist, and the button below them still moves when the bands arrive.

### Deriving your own input

The kit wraps a control when the wrapper meaningfully improves its validation UX — a `<select>`
and a `<textarea>` clear that bar the same way a plain text box always did, which is why
`FormidableInputSelect` and `FormidableInputTextArea` ship beside `FormidableInputText` (see
below). A native `<input>` whose `type` only changes what the browser renders, not how a value
binds — `type="range"`, `type="tel"`, `type="password"` — clears that bar too, without a
dedicated wrapper, by splatting the type straight through `AdditionalAttributes` onto a
string-bound input: `FormidableInputText`'s value binding is a plain `string`, a straight cast
with no conversion and no culture involved, so splatting any of those types onto it works exactly
as splatting `type="date"`/`type="number"` does — the model just stays a string, the way Workout's
own date fields deliberately do, parsing it by hand.

A date or a number input looks like the same case only until the model stops being a string.
`FormidableInputBase<TValue>.AddValueBinding(RenderTreeBuilder, int)` — the base's generic
value-binding overload — serves every `TValue` without special-casing, but what its binder
actually does with the DOM text depends on `TValue`: for `string?` it's an identity, no parsing
and so no culture involved, which is why `FormidableInputText` (and splatting a type onto it) is
free of the problem above. A control binding an actual `DateOnly`/`decimal`/etc. through that
SAME overload would get real parsing instead, done under the *current thread's* culture — while a
native `type="date"`/`type="number"` input's DOM value is a fixed, culture-invariant string (ISO
`yyyy-MM-dd`, period-decimal) regardless. The two can disagree: under a comma-decimal culture the
binder can silently misread `"12.5"` as `125`, and under a non-Gregorian calendar it can misread
the year outright. That gap is exactly why `FormidableInputNumber` and `FormidableInputDate` ship
as dedicated wrappers (see below): each takes a different overload entirely, supplying its own
invariant, format-exact conversion instead of the generic overload's culture-sensitive one. Where a
control needs something the kit provides no wrapper for — a plain `<input type="checkbox">`
whose value binds through `checked` rather than `value`, a third-party component — it stays fully
native instead, either paired with `FormidableFieldAnchor` or driven by `FormidableField`, both
covered below. That rule decides what the kit ships. It doesn't decide what you ship: when a
control in your own form reads better wrapped, derive from `FormidableInputBase<TValue>` and the
derived control gets the same five things the kit's own inputs get. A validated range slider, in
full:

```csharp
public sealed class RatingInput : FormidableInputBase<int>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "type", "range");
        builder.AddAttribute(6, "value", Value);
        AddValueBinding(builder, 7);
        builder.CloseElement();
    }
}
```

*Source: `tests/Formidable.Blazor.Tests/FormidableInputBaseDerivationTests.cs`*

That control comes from the library's own derivation test, quoted whole. The test renders it
inside a `FormidableForm` and asserts what the base hands it: the field registers for progressive
disclosure, the rendered id is the deterministic `FormidableFieldId` that the summary, the message
list and the focus service all address, the state class is appended to whatever `class` the
consumer splatted, and `aria-invalid`/`aria-describedby` follow the field's issues. Consume it
like any kit input — `<RatingInput @bind-Value="_feedback.Rating"
min="0" max="5" />` — where `min` and `max` splat through `AdditionalAttributes` untouched.

Four rules apply to anything derived from the base:

- **Call `AddCommonAttributes` first, `AddValueBinding` last.** The first renders the splat, the
  `id`, the `class` and the aria attributes in the order that merges the consumer's `class` and
  drops their `id`; the last renders the value-commit handler `UpdateOn` asks for. Everything the
  control adds of its own — `type="range"` above — sits between them, after the splat, so it wins
  the duplicate-attribute race too. Writing those four frames by hand instead is what a control
  does when it needs them somewhere the call cannot put them, and it takes the ordering on itself.
- **Call the base when you override `OnParametersSet`.** That is where the field resolves, registers
  and binds its engine subscription, and an override that skips `base` skips all three. Disposal is
  not yours to remember: `Dispose` is non-virtual and always releases the registration and the
  subscription, so a control with resources of its own overrides `DisposeCore` instead. The one
  exception is `IAsyncDisposable`, since Blazor calls only the async overload when a component
  implements both interfaces — a control with a `DisposeAsync` has to call `Dispose()` from it.
- **Leave the two shared-lifecycle hooks alone unless you mean it.** `ObservesEngineState` is why a
  control re-renders when a validation pass lands; overriding it to `false` on something that
  renders a verdict freezes that verdict — the state class, `aria-invalid` and `aria-describedby`
  keep whatever values the last render happened to give them, and `aria-required` stops following
  the rules the same way. `OnEngineStateChanged` is the relay itself: override it to
  do something extra on every pass, and call `base` so the re-render still happens. Which field the
  control speaks for is not adjustable at all — `Register` is sealed on `FormidableInputBase`,
  because an input that resolved a different field, or none, would render no id, register nothing
  for disclosure, and have no field to read state or issues for.
- **Reach the rest through `Context`.** The protected cascaded `FormidableFormContext` is the
  route to everything the five don't cover: `Context.Engine.GetIssues(Field)` to render messages
  yourself, `Context.EditContext`, `Context.Registry`.

`UpdateOn` costs the one `AddValueBinding` call in the snippet above and nothing else, so a
derived control honours every mode — `OnChange`, `OnInput`, `OnBlur`, and whatever the enum grows
next — the same way `FormidableInputText` does, without writing its own ternary that would need
updating every time the enum does.

One more seam serves the number-and-date-shaped control: one whose native element can display
text it reports as empty (a number box holding `e3`, say), which no render-tree diff can
overwrite because the rendered value and the reported value already agree. The kit's own two
cases reconcile the box on blur through `IFormidableDomValueSync`, and a derived control opts
into exactly the same mechanism with two overrides plus the injected service:

```csharp
[Inject]
private IFormidableDomValueSync DomValueSync { get; set; } = default!;

protected override bool SyncsDomValueOnBlur => true;

protected override ValueTask SyncDomValueAsync() =>
    DomValueSync.SyncValueAsync(ElementId, FormatCurrentValue());
```

That is the shape `FormidableInputNumber` itself ships, with `FormatCurrentValue()` standing in
for however the control formats its `Value` back into DOM text. The flag makes `AddValueBinding`
bind `blur` in every `UpdateOn` mode; the write is the override's whole job. Everything else
about the blur stays the base's, in the order it guarantees: the consumer-splatted `@onblur`
runs first, the sync follows, and — under `OnBlur`, while a commit has left a notification
pending — the engine notification is delivered last, so a live pass always renders against a box
that already matches the model.

## `FormidableInputSelect<TValue>`

A `<select>` needs the same extras as a text box — registration, css class, aria, pending,
identity — plus one more: the option a visitor picks is always a string, and the field it drives
usually isn't. `FormidableInputSelect<TValue>` renders the element and `ChildContent`'s
`<option>`s, and converts between the two the same way Blazor's own `InputSelect<TValue>` does:

```csharp
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "select");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", formattedValue);
        AddValueBinding(builder, 6, formattedValue, TryCommitAsync);
        builder.AddContent(8, ChildContent);
        builder.CloseElement();
    }
```

*Source: `src/Formidable.Blazor/FormidableInputSelect.cs`*

That is the same `AddValueBinding` the text box calls, in its string-projected overload: the
control hands over the formatted value it just rendered and a try-parse-and-commit step that
reports whether it actually committed, and the base owns the rest — which event to bind, whether
to notify the engine once the step returns, and the `SetUpdatesAttributeName("value")` that keeps
the rendered `value` frame in step with the browser. A binder concern that reaches one kit input
therefore reaches them all.

The overload honours `UpdateOn`, with one coercion: a `<select>` has no meaningful `input` event
distinct from `change`, the way there is for a text box, so `OnInput` behaves exactly like
`OnChange` (the default) — both bind `onchange` and notify the moment `TryCommitAsync` reports a
value committed. `OnBlur` still commits on that same `change` event, but the notification the
commit arms defers to `blur` instead — the same commit/notify split every other kit input gives
that mode, riding the same `onblur` chaining; a string that fails to parse commits nothing and
arms nothing, so the blur that follows delivers nothing.

Conversion mirrors native closely — the same
`BindConverter.TryConvertTo<TValue>` native's own `InputSelect` calls internally, with the same
`bool`/`bool?` special case (`BindConverter` reserves boolean conversion for conditional HTML
attributes, not form values):

```csharp
    private static bool TryParseValue(string? value, out TValue? result)
    {
        try
        {
            if (typeof(TValue) == typeof(bool))
            {
                if (bool.TryParse(value, out var boolValue))
                {
                    result = (TValue)(object)boolValue;
                    return true;
                }
            }
            else if (typeof(TValue) == typeof(bool?))
            {
                if (string.IsNullOrEmpty(value))
                {
                    result = default;
                    return true;
                }

                if (bool.TryParse(value, out var boolValue))
                {
                    result = (TValue)(object)boolValue;
                    return true;
                }
            }
            else if (BindConverter.TryConvertTo<TValue>(value, CultureInfo.CurrentCulture, out var parsedValue))
            {
                result = parsedValue;
                return true;
            }

            result = default;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(typeof(FormidableInputSelect<TValue>))} does not support the type '{FriendlyTypeName.Of(typeof(TValue))}'.", ex);
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputSelect.cs`*

`TValue` can be `string`, `bool`, an enum, or anything else `BindConverter` converts from a
string — the same set native `InputSelect<TValue>` supports, no broader. A `TValue` it cannot
convert at all throws `InvalidOperationException` the first time a change commits, exactly as
native does; a committed value that merely fails to parse (should not happen when every
`<option>`'s `value` was formatted the same way `FormatValueAsString` formats it) leaves the
model untouched instead of surfacing a parse error — Formidable has no native-parse-error side
channel the way `InputBase<TValue>` does, so FluentValidation stays the only source of validation
truth. Multi-select (an array-typed `TValue`, native's `multiple` mode) is out of scope. One
divergence from native, and the only one: a null `bool?` formats as no selection (a blank
`<option>`), not the `"false"` string native formats it as. That is what lets a blank option
clear a `bool?` field back to unanswered.

One labeling gotcha a text box doesn't have: a `<label>` wrapping a `<select>` has text content
that includes every `<option>`'s own text, not just the label's — which defeats an exact-match
label lookup in test tooling (Playwright's `GetByLabel(..., Exact: true)`, for instance), even
though nothing about it is an accessibility defect. `/foreign` and Workout's tier select both
sidestep it with an explicit `for=` rather than wrapping; `Category` does the same, addressed by
its id, computed the same deterministic way the component computes it internally — through the
expression overload of `FormidableFieldId.For`, so there's no `nameof` step to keep in sync with
the property it names:

```csharp
    private string CategoryId => FormidableFieldId.For(_post, p => p.Category);
```

*Excerpt from `samples/Formidable.Sample/Pages/CustomProfiles.razor.cs`*

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Category`, required under the `Submit` ruleset exactly like `Slug`, is the select, carrying
`UpdateOn="OnBlur"` so picking an option commits it at once while the message waits for the blur
that delivers the commit's notification.

## `FormidableInputTextArea`

The multiline sibling of `FormidableInputText`: same base, same extras, same `UpdateOn` choice —
the only difference is the element tag, mirroring native `InputTextArea` exactly:

```csharp
public sealed class FormidableInputTextArea : FormidableInputBase<string?>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "textarea");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
```

*Source: `src/Formidable.Blazor/FormidableInputTextArea.cs`*

Unlike a `<select>`, a `<textarea>`'s accessible name from an implicit label wrap behaves the
same as an `<input>`'s — its content is its value, not enumerable child elements — so the usual
`<label>Body <FormidableInputTextArea .../></label>` wrap works exactly the way it does for
`FormidableInputText`.

**Sample:** [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) — `Body` is the
textarea.

## `FormidableInputNumber<TValue>`

A number needs the same five extras as a text box, plus one thing a text box's binding can't
give it: an HTML `<input type="number">`'s DOM value is always period-decimal — `"12.5"`, never
`"12,5"` — regardless of the browser's locale, but `AddValueBinding`'s typed overload resolves
the current thread's culture. Under a comma-decimal culture, that overload would silently misread
`"12.5"` as `125` rather than failing loudly, so `FormidableInputNumber<TValue>` converts through
`CultureInfo.InvariantCulture` instead, using the string-projected overload with its own
parser rather than the base's own conversion:

```csharp
    static FormidableInputNumber()
    {
        var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (targetType != typeof(int) &&
            targetType != typeof(long) &&
            targetType != typeof(short) &&
            targetType != typeof(float) &&
            targetType != typeof(double) &&
            targetType != typeof(decimal))
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(typeof(FormidableInputNumber<TValue>))} does not support the type '{FriendlyTypeName.Of(typeof(TValue))}'. " +
                "Supported types are int, long, short, float, double, decimal, and their nullable forms.");
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputNumber.cs`*

`TValue` is checked once, in a static constructor, against the same set native
`InputNumber<TValue>` supports — `int`, `long`, `short`, `float`, `double`, `decimal`, and their
nullable forms. An unsupported `TValue` never gets as far as an instance: the runtime wraps the
thrown `InvalidOperationException` in a `TypeInitializationException` the first time the closed
generic type is touched, the standard shape for any failing static constructor.

```csharp
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "step", "any");
        AddCommonAttributes(builder, 2);
        builder.AddAttribute(6, "type", "number");
        builder.AddAttribute(7, "value", formattedValue);
        AddValueBinding(builder, 8, formattedValue, TryParseValue);
        builder.CloseElement();
    }
```

*Source: `src/Formidable.Blazor/FormidableInputNumber.cs`*

`step="any"` renders first, *before* `AddCommonAttributes`' splat — the consumer-wins position,
the opposite of `type` below. HTML's own default `step` is `1`, which makes any fractional value
a native `stepMismatch`. Under `FormidableForm` that mismatch does not block the submit,
because the form renders `novalidate` (see [above](#formidableformtmodel)); what `novalidate`
leaves running is the constraint computation itself, so the field would still match `:invalid`
while it held the fraction, and the spinner buttons would still snap to whole numbers. In a form
without `novalidate` — a consumer's own `EditForm` in attach mode, or a `FormidableForm` whose
splat removed the default — the browser additionally blocks a real `<button type="submit">`
before it reaches Blazor at all and shows its own constraint-validation tooltip, not
FluentValidation's message. The default `step="any"` retires the mismatch at the source, in
every one of those forms, so only FluentValidation judges a fractional value — matching native
`InputNumber<TValue>`'s own default for every one of its supported types. Rendering it before
the splat, rather than forcing it the way `type` is forced, means a consumer's own splatted
`step` overrides the default outright, taking back whatever native handling of a mismatch their
form's `novalidate` answer has left on.

`type="number"` renders in the component-wins position, after `AddCommonAttributes`' splat, the
same spot `RatingInput`'s `type="range"` takes above. `AddValueBinding` here is a third overload —
string-projected like `FormidableInputSelect`'s, and honouring `UpdateOn` the same way, but with a
synchronous try-parse (`StringValueParser`, culture-invariant) in place of `FormidableInputSelect`'s
async try-commit, and a real `OnInput` rather than a coercion to `OnChange`: a number box fires a
genuine `input` event per keystroke the way a `<select>` never does, so this overload binds it
distinctly, including the same consumer-`@onblur`-chains-first contract under `OnBlur`. A control
that needs invariant string conversion without losing `UpdateOn` is what this overload is for;
`FormidableInputDate` below is the kit's other case.

A string that fails to parse — including an emptied box when `TValue` is not nullable — leaves
the field uncommitted: the model stays what it was, and under `OnBlur` no notification is armed,
so the blur that follows has nothing to deliver. The box itself is squared with the model on
`blur`. A native number input admits the characters of scientific notation, so it can hold text
like `e3` that it *displays* while reporting an empty value to every event — and no render-tree
diff can overwrite a difference it cannot see. Every time focus leaves the field, the control
therefore writes the model's formatted value straight into the element
(`IFormidableDomValueSync`, registered by `AddFormidableBlazor()`): whatever the box showed, it
ends up matching the model. Model an optional number as `int?`, `decimal?`, and so on, so an
emptied box commits `null` instead — a rule like `NotNull()` can then say so, and FluentValidation
stays the only source of a message a visitor sees.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Read minutes`, required and range-checked under the `Submit` ruleset.

## `FormidableInputDate<TValue>`

The same problem, one step further: a native `<input type="date">`'s DOM value isn't just
period-decimal, it's a specific calendar-and-format pair — ISO `yyyy-MM-dd`, always, regardless
of locale. Under a non-Gregorian-calendar culture such as Thai (Buddhist calendar), the base's
current-culture conversion can read `"2024-01-15"` back as a different year entirely rather than
failing, so `FormidableInputDate<TValue>` formats and parses through that exact format string
under `CultureInfo.InvariantCulture`:

```csharp
    private static string? FormatValueAsString(TValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => BindConverter.FormatValue(dateTime, IsoDateFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => BindConverter.FormatValue(dateTimeOffset, IsoDateFormat, CultureInfo.InvariantCulture),
            DateOnly dateOnly => BindConverter.FormatValue(dateOnly, IsoDateFormat, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
```

*Source: `src/Formidable.Blazor/FormidableInputDate.cs`*

`TValue` is checked the same way `FormidableInputNumber` checks its own — a static constructor
against `DateTime`, `DateTimeOffset`, `DateOnly`, and their nullable forms, failing the same
`TypeInitializationException`-wrapped way for anything else. Rendering follows the identical
shape: `type="date"` in the component-wins position, `AddValueBinding`'s
string-projected-but-`UpdateOn`-honouring overload doing the parsing.

Prefer `UpdateOn="InputUpdateMode.OnBlur"` for this component specifically. Chromium fires a
native date input's `change` event once per typed segment — day, month, year — rather than once
per completed date, so the default `OnChange` can run a live pass, and briefly show a stale
verdict, against a year the visitor hasn't finished typing. Under `OnBlur` the model still commits
on every segment's `change` (so a wrapping form always reads the field's current value), but the
engine is notified only once, on `blur`, once the value has had a chance to settle — and only
because those segment commits armed it: tabbing through without committing anything notifies
nothing. It is the same
per-segment problem [Options](options.md#updateon-per-input-not-a-formidableoptions-property)
covers for the general case, answered by the typed input directly rather than by splatting
`type="date"` onto a text box.

The same uncommitted-value, blur-sync, and nullable-modelling rules as `FormidableInputNumber`
apply: an unparseable or emptied non-nullable box leaves the model untouched, every blur writes
the model's value back into the element so half-entered segments cannot linger, and modelling an
optional date as `DateOnly?`/`DateTime?`/`DateTimeOffset?` lets an emptied box commit `null` for
a rule such as `NotNull()` to judge.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Publish date`, required under the `Submit` ruleset.

## `FormidableFieldMessage<TValue>`

Every field needs somewhere to show what's wrong with it. `FormidableFieldMessage` renders a
field's current issues, any severity, as an accessible list. The list element itself renders
always — empty when the field has none — so a consumer's CSS can transition it open and closed the
way the sample transitions [`FormidableSummary`](#formidablesummary), whose wrapper and regions
persist the same way while its bands come and go, and so a configured `InlineMessageLive` sits on
an element that persists across renders rather than one that enters alongside the text it
announces (see [Options](options.md#inlinemessagelive)). It shares one base
(`FormidableMessageBase<TValue>`) with its collection-level sibling for subscribing to the
engine's `StateChanged` and rendering that same list; resolving `For` comes from
`FormidableAccessorComponentBase<TValue>`, a level further up — the same base `FormidableField`,
`FormidableFieldAnchor` and `FormidableRequiredIndicator` build on. Both bases are public only
because a public component cannot inherit a less accessible base; neither's constructor is, so
`FormidableFieldMessage` and `FormidableCollectionMessage` are the only two shapes
`FormidableMessageBase<TValue>` takes. Unlike `FormidableInputBase<TValue>`, neither is an
extension point.

`AdditionalAttributes` splats onto that list element under the kit's usual ordering: the
consumer's attributes enter the render tree first and the computed ones after, so a computed
value wins the duplicate. A splatted `class` is merged rather than replaced, the splatted value
first and `formidable-message-list` after. A splatted `id` is ignored, because the rendered id is
the `aria-describedby` target every input describing itself by this list points at. And while
[`InlineMessageLive`](options.md#inlinemessagelive) is set, the `aria-live` it configures wins a
splatted one: the list's live-region behaviour is that option's to decide, form-wide. With the
option unset the kit computes no `aria-live`, so a splatted one stands.

One base method decides whether rendering a message list also registers the field it lists:

```csharp
    /// <summary>
    /// Registers <paramref name="field"/> with <paramref name="context"/>'s field registry, or
    /// returns null to skip registration. Messages are not inputs, so the base implementation
    /// (used by <see cref="FormidableFieldMessage{TValue}"/>) never registers;
    /// <see cref="FormidableCollectionMessage{TValue}"/> overrides this to mark its
    /// collection-level path revealed so collection-level rules
    /// surface even though the collection itself has no validated input registering it.
    /// </summary>
    private protected virtual FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) => null;
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

`FormidableFieldMessage` takes the base's default — it never registers:

```csharp
/// <summary>
/// Renders a field's current validation issues (any severity) as an accessible message list; the
/// list itself renders always, empty when the field has none. Does not register with the field
/// registry — messages are not inputs, so pairing a message with a validated input (or a
/// <see cref="FormidableFieldAnchor{TValue}"/>) elsewhere in the form is what keeps the field
/// revealed.
/// </summary>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

Practically: `FormidableFieldMessage` always needs to be paired with something else that registers
the same field — a `FormidableInputBase` descendant, `FormidableField`, or `FormidableFieldAnchor`
— or nothing a submit finds for that field ever reaches the list. The live channel is the
exception, and a deliberate one: it discloses an engaged field's verdict without consulting
registration (see [Disclosure](disclosure.md#the-live-channel-plays-by-its-own-rule)), so an
unpaired list can carry live messages while staying silent about everything submit revealed. Its
collection-level sibling, `FormidableCollectionMessage`, overrides that same hook to skip the
pairing requirement entirely; it's introduced below, once collections are in scope. Its
model-level sibling renders the same list for the issues no `For` expression can reach, and is
next.

## `FormidableModelMessage`

Some verdicts are about the form rather than about any field: the defensive gate's explanation
for a blocked submit that can show nothing (see [Disclosure](disclosure.md)), a server error
applied with an empty path, and the fault issue a throwing live rule leaves behind, which
`GetIssues` orders last. They live on the model-level field — an empty
`FieldIdentifier.FieldName` — which `FormidableSummary` lists and the `EditContext`'s store
carries, but which no `FormidableFieldMessage` can name: `For` takes an accessor expression, and
no expression reaches the model-level field. A form built on inline messages alone would
therefore show the gate, the one message whose whole purpose is being seen, nowhere.

`FormidableModelMessage` is that surface. Parameterless, because the field it speaks for is
fixed:

```razor
    <FormidableForm Model="_inlineRequest" Options="_inlineOptions"
                    OnValidSubmit="HandleInlineValid">
        <FormidableModelMessage />
```

*Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor`*

It renders the same persistent list as the field messages, through the same implementation: the
always-rendered `<ul class="formidable-message-list">`, empty while the form has nothing to say,
items entering and leaving as `formidable-message formidable-message--{severity}`. Its id is the
model-level message id — the form element's own id plus `-messages`, per
`FormidableFieldId.MessagesFor` — and a configured
[`InlineMessageLive`](options.md#inlinemessagelive) sits on the persistent element. That
announcement is the point on a summary-less form: the gate's explanation arrives inside a live
region assistive technology already knew about, rather than in an element inserted alongside the
text it should announce. The splat and its policies are the message components' own (a splatted
`class` merges, the computed `id` wins, a configured `aria-live` wins a splatted one).

It registers nothing. Registration is how a *field's* submit errors earn disclosure; the
model-level field is always disclosed, because its element is the form's own, on the page for as
long as the form is. Beside a `FormidableSummary` the component is redundant — the summary
already lists model-level issues among everything else — so it earns its place on the form that
has no summary. Attach mode changes nothing about the component: `FormidableValidator` cascades
the same context, so it renders the same list inside a consumer's own `EditForm`. What attach
mode does change is what points at that list. `FormidableForm` describes its own `<form>` element
with the list's id; `FormidableValidator` renders no `<form>` and cannot reach the one the page
owns, so the page writes that `aria-describedby` itself, beside the gate id — see
[the attach-mode section below](#formidablevalidatortmodel-attaching-to-an-existing-form).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the
summary-less variant form under *Without a summary*: submit with the trip details collapsed and
the gate's explanation appears through the component; reveal them and the inline messages take
over while the form-level list empties.

## `FormidableRequiredIndicator<TValue>`

Marks a field the submit profile demands a value for. It renders
`<span class="formidable-required" aria-hidden="true">` around
[`FormidableOptions.RequiredIndicatorContent`](options.md#requiredindicatorcontent) — `"*"` unless you say
otherwise — and nothing at all for a field the rules do not demand:

```razor
    <div class="field"><label>Title <FormidableRequiredIndicator For="() => _proposal.Title" /> <FormidableInputText @bind-Value="_proposal.Title" /></label>
        <FormidableFieldMessage For="() => _proposal.Title" /></div>
```

*Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor`*

Inside the `<label>`, after the label text, is where most samples put it. The mark is derived from
the validator's rules rather than declared on the markup, so a presence rule moving between
profiles moves the mark with it and a form cannot drift out of step with what it enforces. The
submit profile is the one that decides, because "required" on a form means "required before this
can be submitted": a narrowed `LiveProfile` changes when a message appears, never whether the
value is demanded.

That placement is the samples' habit, not the component's contract. It needs exactly two things,
the cascaded form context and `For`, and reads nothing from the label or input around it, so the
mark lands wherever your markup puts it: before the label text, after the control, outside the
`<label>` entirely. A checkbox is where the habit inverts — the control comes first, so the mark
follows it:

```razor
<FormidableField For="() => _signup.AcceptsTerms" Context="field">
    <label><input type="checkbox" @attributes="field.InputAttributes"
                  checked="@_signup.AcceptsTerms"
                  @onchange="args => ToggleTerms(args, field)" />
        <FormidableRequiredIndicator For="() => _signup.AcceptsTerms" />
        I accept the terms</label>
</FormidableField>
```

The splatted `field.InputAttributes` carries the accessible half, putting `aria-required="true"`
on the checkbox while the rules demand it. One note on the rule such a field needs: "must be
ticked" is readable as a demand only when written as FluentValidation's own `NotEmpty()` on the
`bool`, whose empty value is `false`. Written as `Equal(true)` or a `Must(...)`, it is
indistinguishable from any other predicate and answers `NotRequired` (see the detection limits
below), so the mark then waits on [`RequiredOverride`](options.md#requiredoverride) — a limit of
reading rules, not of the component. Beside a native input the component works the same way:
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)'s venue field places it next to a
plain `InputText`, wiring the accessible half by hand. A radio group is where the mark leaves the
label altogether: [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) puts the
accommodation question's mark in the group's `<legend>`, outside both option labels and outside
the `FormidableField` wrapping the radios, since each label there names an option rather than the
field.

It registers nothing, since a marker is not an input, so what keeps the field registered for
disclosure is the validated input beside it or a `FormidableFieldAnchor`, exactly as it is for
`FormidableFieldMessage`. It observes no engine state either. Requiredness is a property of the
rules rather than of what the values are doing, so no validation pass changes what it draws; a
change to `RequiredOverride` or `SubmitProfile` is picked up on the page's next render.

**It is `aria-hidden`, deliberately, and it is not the accessible half of this feature.** A glyph
read aloud inside a label announces the field as "Title star". The fact belongs on the input,
where the kit's inputs and `FormidableFieldContext.InputAttributes` put it as
`aria-required="true"`. That is also what lets the mark sit inside a `<label>` without disturbing
the accessible name computed from it. See [CSS and
accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby).

Requiredness is a three-valued answer, `FieldRequirement`, and the component draws for one of them:

| `FieldRequirement` | What it means | What the component draws |
|---|---|---|
| `Required` | The submit profile selects a presence rule for the field, and it carries no condition | The marker |
| `ConditionallyRequired` | Every presence rule the profile selects for the field is conditional | Nothing |
| `NotRequired` | No presence rule was found for the field | Nothing |

`ConditionallyRequired` draws nothing because a condition cannot be evaluated without a model
instance: inspection can see that the demand exists without being able to say whether it applies
to the model in hand. Drawing the same mark would assert a demand the library cannot verify, and a
validator whose presence rules are all conditional would mark every field on the form. A page that
wants to say something there reads `FormidableFieldContext.Requirement` from a `FormidableField`
and renders its own markup, or declares the field outright with
[`RequiredOverride`](options.md#requiredoverride).

A field inside a collection row is marked exactly as a top-level field is. The rules declare it
with the index left open (`Attendees[].Name`) — the rule speaks about the shape — and the answer
is expanded against the rows the model actually holds, one entry per row under the row's own
identifier, so each attendee's Name earns its own marker and its own `aria-required`, and a row
added later is answered by the derivation that follows its arrival. A collection rule that
filters its rows (`RuleForEach(...).Where(...)`) is conditional the same way a `When` is — which
rows the filter admits cannot be answered without a model — so its demands answer
`ConditionallyRequired` for every row, the answer being a property of the rules rather than of
any one row's values.

Detection has limits, and they cost a mark rather than producing a wrong one:

- Presence has to be written as FluentValidation's own `NotEmpty()` or `NotNull()`. Written as a
  predicate, `Must(s => !string.IsNullOrWhiteSpace(s))`, it is indistinguishable from any other
  predicate.
- A validator that cannot be inspected reports nothing for any of its fields.

Scoping is read as FluentValidation executes it. A child validator scoped by the `SetValidator`
call itself (`RuleFor(x => x.Address).SetValidator(new AddressValidator(), "Admin")`) runs under
a selection built from those ruleset names, which replaces the one that selected the rule holding
the child — and it is read under that same selection. A rule inside `AddressValidator` tagged
into `"Admin"` demands its field whenever the holding rule is selected; an untagged `NotEmpty()`
in there is one FluentValidation runs under no profile, so it demands nothing anywhere, and the
absent mark is the honest report of a rule no submit enforces.

A rule the root does not declare for itself **is** read, by whichever of three routes carries it:
one inside a child validator answers under the child's own path (`Address.City`), one merged in
with `Include` at the including validator's level, and one inside a child a model-level rule
carries at the root's own level.

`NotRequired` therefore means "not known to be required", never "proven optional". Where the rules
cannot be read, [`RequiredOverride`](options.md#requiredoverride) is what a form says instead, and
it decides the marker and `aria-required` together so the two cannot disagree.

The library ships no styling, so `formidable-required` is a hook for your stylesheet and
[`RequiredIndicatorContent`](options.md#requiredindicatorcontent) supplies the text inside it —
set that to `""` and the marker renders empty, which keeps the element on the page for a
stylesheet's `::before` to draw a CSS-only glyph into. Setting
[`ShowRequiredIndicators`](options.md#showrequiredindicators) to `false` turns every marker on
the form off at once — no element at all — and leaves `aria-required` exactly where it was.

One practical note for tests: a query matching raw text sees the marker, and a role-and-name
query, which runs the accessible-name algorithm, does not. `aria-hidden` is what separates the
two, and it is the second kind of query that reflects what a visitor using assistive technology
hears. Which raw text is the one that sees it depends on where the mark sits — a label-embedded
marker lands in the label's text, one in a `<legend>` in the group's.

**Sample:** [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor) — three marked
fields, one of which is marked and silent at the same time;
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor), with required marks on kit inputs,
a foreign select, a native input and the attendee rows, and the unmarked fields carrying the same
component; and [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor), where one
mark sits outside the label entirely, in a radio group's `<legend>`.

## `FormidableSummary`

Renders a live, severity-grouped list of the currently-visible issues across the form — every one
of them until you say otherwise — inside
markup built to be announced: one persistent `<div class="formidable-summary">` wrapper holding
fixed-role live regions that render from the first paint and stand empty while the form has
nothing to show. Errors band into a region that has carried `role="alert"` since it rendered;
warnings and infos band into one that has always carried the politer `role="status"` — an
errors-free submit that surfaces only advisories should not interrupt the way a blocking error
does. No role ever changes on any element, and every issue that arrives after a region's own first
render lands inside a live region whose role was already there: the shape assistive technology
announces reliably, where a role entering the DOM in the same render as the content it should
announce is the shape that gets dropped. `Show` decides which regions exist, and it is a
parameter, so changing it at runtime while matching issues are already on screen is where a region
and its first band still share a render:

```csharp
    private const string ErrorsRegionClass = "formidable-summary__region formidable-summary__region--errors";
    private const string AdvisoriesRegionClass = "formidable-summary__region formidable-summary__region--advisories";
```

```csharp
        builder.OpenElement(0, "div");
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "class", FormidableCss.CombineClassNames(AdditionalAttributes, "formidable-summary"));

        if (Show is SummaryFilter.All or SummaryFilter.Errors)
        {
            builder.OpenRegion(3);
            BuildRegion(builder, ErrorsRegionClass, "alert", visibleIssues, errorRegion: true);
            builder.CloseRegion();
        }

        if (Show is not SummaryFilter.Errors)
        {
            builder.OpenRegion(4);
            BuildRegion(builder, AdvisoriesRegionClass, "status", visibleIssues, errorRegion: false);
            builder.CloseRegion();
        }
```

*Excerpt from `src/Formidable.Blazor/FormidableSummary.cs`* — `BuildRegion` renders one region
element carrying the class and fixed role it is handed, then whichever severity bands currently
belong inside it. The `OpenRegion` calls give each region its own sequence-number space, which is
what makes the persistence real at the DOM level: Blazor's diff matches sibling frames by
sequence number, so without the isolation the advisories region's number would shift with the
error content ahead of it and the diff would replace the element instead of keeping it.

`AdditionalAttributes` splats onto that wrapper and reaches nothing below it: the fixed-role
regions, and every element the component builds inside them, are contract, so a consumer cannot
re-role a region or decorate anything under one by splatting. On the wrapper the kit's usual
ordering applies, which the excerpt's first three calls spell out: the element opens, the splat
enters, and the computed `class` follows it. So a splatted `class` is merged rather than
replaced — the splatted value first, then `formidable-summary`.

Each item is a button, and a click on it runs the component's own `ActivateAsync`, which moves
focus to the offending field through `IFormidableFocusService`:

```csharp
                builder.AddAttribute(entrySequence++, "onclick", EventCallback.Factory.Create(this, () => ActivateAsync(entry)));
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

Click-to-focus reaches a rendered element that will take focus. A row scrolled out of a
virtualized container's window has no DOM element yet, and an element inside a collapsed section
has one that refuses; either way the click lands nowhere and the entry is a miss.
`FocusFallback` is the escape hatch for that gap,
covered once the seams that need it are in view — see [FocusFallback](#focusfallback) below, and
[PrepareFocus](#preparefocus) beside it for a field the caret can reach but the visitor cannot see.

### The order entries appear in

Entries follow the page. Within each severity group, issues are listed in the document order of
the fields that render them, so the first error a visitor reads about is the topmost one rather
than whichever rule the validator happened to declare first. Two issues on one field keep the
order the validator produced them in.

`FormidableForm` is what supplies that order. After any render that changed the set of registered
fields — or one a browser-side observer reports moved the form's existing elements around without
registering or unregistering any of them — it asks `IFormidableFieldOrderService` where those
fields sit and hands the answer to its engine, which sorts `GetVisibleIssues()` by it. The browser
is the only thing that knows where an element is, so the shipped implementation is JS-backed — and
public for the same reason `IFormidableFocusService` and `IFormidableDomValueSync` are, so a bUnit
test can fake the seam instead of standing up module interop (see
[Testing](testing.md#the-form-under-bunit)).

The seam's currency is the field, not its rendered element id:

```csharp
    ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields);
```

*Source: `src/Formidable.Blazor/IFormidableFieldOrderService.cs`*

A `FieldIdentifier` maps to the id its element carries through
`FormidableFieldId.For(field)`, so a field-based seam can express DOM position and anything else
an implementation knows about a field; an id alone can only ever express the first, since it
cannot be read back into the field it came from. That round trip belongs to the implementation
that wants it — the shipped one maps each field to its id, asks the browser to sort by
`compareDocumentPosition`, and maps the answer back.

Four edges of that contract are worth knowing, all deliberate:

- **A verdict about the whole form is reported first.** The request is not only the registered
  fields: the model-level field rides along on every resolution, and its element is the `<form>`
  the component renders, which contains every field on the page. Document order therefore puts it
  ahead of everything else, which is where the all-suppressed gate's explanation and a validator
  fault belong — they address the form, not a field inside it. An implementation need not
  special-case it: `FormidableFieldId.For` derives its id the same way it does for any other
  field.
- **A field the page cannot place sorts last.** The service answers only for the fields it can
  actually locate, so anything it leaves out sorts after everything it placed: a control that
  renders no id of its own, and a row held only by
  [`KeepRegistered`](#virtualize-and-keepregistered), which stays registered precisely because it
  has left the DOM. There is nowhere on the page to send a visitor for either of them anyway.
- **An empty answer and no answer are different answers.** An empty list says none of these
  fields are on the page, which is a legitimate thing to say about a page and is taken as the
  order. `null` says the order could not be resolved at all, and the form treats it as "ask again
  on a later render" — the same way it treats an interop call that threw. Answer `null` where an
  empty list was meant and the form re-resolves on every render; answer empty where `null` was
  meant and it settles on validator order until the registered field set next changes, which on a
  stable form is never.
- **Before the first resolution, the order is the engine's own.** A resolve lands after the render
  that produced the elements, so until one has, `GetVisibleIssues()` reports channel by channel:
  the fault issue, then submit errors, then advisories, then the live channel. The same is true of
  a host that never resolves an order at all — `FormidableValidator` in attach mode, or an app
  that registered no order service — which keeps the summary working and costs it only the
  reading order (see [Migration guide](migration-guide.md#what-to-check-after-migrating)).

Document order is the default because it is the order a visitor reads the form in. A form that
wants a different one sets [`FormidableOptions.OrderIssues`](options.md#orderissues), a
synchronous re-sort over the resolved order, rather than implementing the seam — the seam is for
answering *where* the fields are, which is the part only the browser can do. Sorting by something
the layout decides, such as a two-column or `flex`-ordered form, is the case that genuinely needs
the seam: see [Recipes](recipes.md#i-want-the-summary-ordered-by-where-fields-appear-on-screen).

### Showing one severity band

`Show` picks which severities a summary renders. It defaults to `SummaryFilter.All` — the single
combined list above — and the other four members (`Errors`, `Advisories`, `Warnings`, `Infos`)
narrow it, `Advisories` meaning every non-error issue, exactly as `ValidationReport.Advisories`
does: warnings, infos, and any severity outside those two. A page that wants the blocking
problems apart from the commentary renders two:

```razor
<FormidableSummary Show="SummaryFilter.Errors" />

<fieldset>
    ...
</fieldset>

<p>Worth a look before you send this:</p>
<FormidableSummary Show="SummaryFilter.Advisories" />
```

`Show` also decides which fixed-role regions the wrapper holds: `All` renders both, `Errors` the
`role="alert"` region alone, and the three advisory filters the `role="status"` region alone. A
filter that matches nothing renders its region empty, the same as a clean form — the region
persists so that whatever arrives in it later is announced from an element already carrying its
role. The paragraph above a quiet advisory summary is still the page's to hide: key it on the
summary's bands (`:has(.formidable-summary__band)`), which exist exactly while the summary has
something to say, the way the sample's own slide does.

Each summary reads the same visible issues and filters them independently, which has two
consequences worth planning around. A default summary rendered alongside a filtered one shows
those issues twice — nothing coordinates between them, so pick one shape per form. And every
region is a live region of its own, so two summaries are two live regions and two announcements:
an `Errors` summary announces from its `alert` region whenever it has anything, an `Advisories`
one always from the politer `status` (see
[CSS and accessibility](css-and-accessibility.md#formidablesummary-as-a-live-region)).

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — two disjoint
summaries stacked one above the other at the top of the form, so what blocks and what merely
advises arrive as separate blocks. The one with nothing to say takes no space: its wrapper and
region persist for announcement's sake, while everything visible — panel, tint, spacing — lives
on the bands it currently has none of.

### Heading each band

`ErrorsHeading`, `WarningsHeading` and `InfosHeading` (all `string?`, all `null` by default) give a
severity band its own heading. Set one and the summary renders it as an `h{HeadingLevel}` (`2` by
default, any of `1`–`6`; anything else throws from `OnParametersSet`) inside that band, with an id
this component mints and wires to the band's `<ul>` via `aria-labelledby` — the relationship is the
summary's to get right, not a consumer's to reconstruct. Leave a heading unset and neither the
element nor the attribute appears, so a summary with none of the three set renders exactly as one
that never mentions them. No English default stands in for a heading you leave unset: a band's
label is your own wording in your own language, and the summary has no way to guess either.

The band itself (`<div class="formidable-summary__band formidable-summary__band--{severity}">`,
wrapping the heading, if any, and the `<ul>` together) renders for every band regardless of
whether that band carries a heading, so a stylesheet has one consistent shape to target rather than
a wrapper that appears only once a heading is set. It sits inside its severity's fixed-role
region, between that region and the `<ul>`, which matters to anyone styling with child
combinators: the chain is `.formidable-summary` > `__region` > `__band` > `ul`, so a
`.formidable-summary > ul` selector matches nothing — descendant selectors are the durable
choice. What the band buys is a panel per severity to style
as one — background, left rule, radius, padding on `.formidable-summary__band` rather than spread
across the `<ul>` and its `formidable-summary__group` class. The sample styles it exactly that
way; where a consumer's own panel styling lives is theirs to decide, since Formidable ships no CSS
of its own (see [CSS and accessibility](css-and-accessibility.md)).

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — the errors
summary carries `ErrorsHeading`, and the advisories summary beside it carries both
`WarningsHeading` and `InfosHeading` for its two bands.

### Deciding what an entry says

`ItemTemplate` replaces what an entry's button contains. It receives the entry's `VisibleIssue` —
the field and the issue together — so it can render anything either of them carries. The common
reason to reach for it is the field's name rather than the rule's complaint: `Issue.DisplayName`
is the `WithName(...)` value, or FluentValidation's own split of the property path where no
`WithName` was given.

```razor
<FormidableSummary>
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
</FormidableSummary>
```

The button around it stays the component's: its element and its `formidable-summary__link` class
are not template-able, so an entry you have reworded still reads to assistive technology as the
button its list item promises, and still matches a stylesheet written against
[the class inventory](css-and-accessibility.md#the-structural-class-inventory). What a click on
that button does stays the component's too, so rewording an entry changes what it reads as and
nothing about where the click takes the visitor. Something wanting different markup *around* the
entries is a summary of your own — [Recipes](recipes.md#i-want-my-own-summary-markup) walks that,
and this component is not in its way.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — a toggle
puts the template in and takes it away again over one blocked submit, so the same seven errors
read as seven messages and then as seven field names.

### One entry per field

A summary lists one entry per issue, so a field failing two rules is listed twice. That is the
right answer for a list of messages and the wrong one for a list of names — "Email" twice reads as
a mistake in the summary rather than a mistake in the form. `GroupByField` collapses each severity
band to one entry per field, keeping the first issue of each and dropping the rest:

```razor
<FormidableSummary Show="SummaryFilter.Errors" GroupByField="true">
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
</FormidableSummary>
```

Three things about it are decided rather than incidental. It groups by **field identity**, not by
display name, because two genuinely different fields are free to carry the same `WithName(...)` and
merging those would drop one of them from a list whose whole job is to be complete — and field
identity is what the click has to resolve anyway. It groups **within a band**: a field carrying an
error and a warning is listed once under each, which is one field described two ways rather than
one description repeated. And the entries keep the position of each field's first issue, so a
grouped band is still in [the order the page is read in](#the-order-entries-appear-in).

Every model-level issue shares one field identifier, so grouping collapses those too: a band
holding both a validator fault and the all-suppressed gate's explanation renders one entry for the
pair.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — its
Ticket reference field breaks two rules, so grouping is the difference between listing that name
once and listing it twice, and the warning band under it never moves.

### Capping the list

`MaxItems` is the most entries a band renders, `null` (the default) meaning all of them. It counts
entries, which is issues by default and fields under `GroupByField` — so `GroupByField="true"`
with `MaxItems="4"` is "the first four fields with a problem", where `MaxItems="4"` alone is "the
first four problems".

```razor
<FormidableSummary Show="SummaryFilter.Errors" GroupByField="true" MaxItems="4">
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
    <OverflowTemplate Context="held">+ @held.Count more to fix</OverflowTemplate>
</FormidableSummary>
```

The cap is per band, not per summary: a band is a list of its own with a heading of its own, and
counting across the summary would let a run of warnings decide how many errors a visitor gets to
read. A value at or above a band's entry count changes nothing. `0` is legal and renders that
band's list with no entries in it, which is how a summary hands `OverflowTemplate` the band whole
and lists none of it itself. A negative value throws from `OnParametersSet`, for the reason an
out-of-range `HeadingLevel` does: `MaxItems` is a number pages arrive at by arithmetic, and
arithmetic that has gone below zero is a mistake worth seeing rather than a list that quietly
empties itself.

`OverflowTemplate` is what a capped band renders in place of what it dropped. It receives the
entries that band held back — never an empty list, since a band that held nothing back never
reaches the fragment — and the component supplies the `<li class="formidable-summary__overflow">`
around it. A line that only counts them reads `Count`, as above. The entries are there for the
summary that wants more than a number out of them: an expander revealing what did not fit, a
tooltip listing it, a line that names the fields rather than counting them. Each entry is the same
`VisibleIssue` `ItemTemplate` gets for a shown one, so you render a held-back entry exactly as you
render a shown one.

Each band offers its own and no other band's, so a summary showing errors and advisories together
renders the fragment once for each band that held anything back.

Leave it unset and a capped band renders **nothing** in place of what it dropped: no element, no
sentence. "And 3 more" is a sentence with a language and a plural rule behind it, and the summary
can pick neither; the line is yours, with your own plural rules.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — its
overflow line is an expander that names what did not fit, and one more toggle takes the fragment
away so the capped band ends in silence instead.

## `FormidableValidator<TModel>`, attaching to an existing form

A form that must attach to an `EditForm` it doesn't own — an existing page already built around a
plain `EditForm`/`EditContext` — doesn't need to give that up to use Formidable. It has an
alternative root, `FormidableValidator<TModel>`, for exactly that case; the full pattern,
including when to reach for it over `FormidableForm`, is covered in
[Migration guide](migration-guide.md).

Attach mode owns the engine, not the form, so the verbs that come with owning an `EditForm` stay
with `FormidableForm`: `SubmitAsync`, `ResetAsync`, the typed submit callbacks and the `Model`
parameter whose swap rebuilds everything all belong to the component that renders the `<form>`. A
page in attach mode keeps its own `EditForm`'s handlers for that half.

What it does not give up is what the submit itself does. `ValidateForSubmitAsync()` runs the
pipeline against this component's engine and hands back the same `SubmitOutcome` the form's own
`SubmitAsync` returns, so a page's `EditForm` handler routes that outcome itself instead of
reaching past the component to `Engine` for the engine's method:

```csharp
private async Task HandleSubmit()
{
    var outcome = await _validator!.ValidateForSubmitAsync();
    if (outcome.CanProceed)
    {
        await SaveAsync(outcome.Report);
    }
}
```

A blocked submit through it lands the visitor on the first error, under the same
`FocusFirstErrorOnInvalidSubmit` switch, clears the way ahead of that move through the same
[`PrepareFocus`](#preparefocus) seam, and recovers a first error that does not take focus
through the same `FocusFallback` seam. All three parameters carry the names, defaults and delegate
shape they carry on `FormidableForm`, so a page that wants one callback recovering both its
summary's clicks and its submit's auto-focus writes that callback once and hands it to each.

That submit is the only focus move this component makes unasked. It also carries
`FocusFirstErrorAsync()`, the same method `FormidableForm` exposes and the same move that submit
makes, for a page that would rather choose the moment — after closing the dialog it opened
instead, say. A `FormidableSummary` nested
inside it goes on moving focus when a visitor clicks an entry, exactly as it does under
`FormidableForm`. What stays still is the server round trip: `ApplyServerIssues` here applies the
verdict and focuses nothing, where `FormidableForm`'s moves, because a round trip is the page's
own and so is what happens after a rejection.

What does still follow from rendering no `<form>` is the reading order rather than the focus.
Nothing resolves where the fields sit, so a summary here lists issues in the engine's own channel
order rather than the page's, and the first error a blocked submit focuses is the first in that
same order. Focus parity is not order parity — see
[Migration guide](migration-guide.md#what-to-check-after-migrating).

`FormidableValidator` cascades its `FormidableFormContext` only to its own `ChildContent` — pass
none and it renders nothing at all, cascade included, rather than an empty wrapper. Everything
that needs the cascade, `FormidableSummary` and any `FormidableInputText`/`FormidableFieldMessage`
pair among them, belongs nested inside `<FormidableValidator>...</FormidableValidator>`, not
sitting beside it as a sibling. The fragment is typed (`RenderFragment<FormidableFormContext>`,
the same shape `FormidableForm`'s body has), and here that always meets one structural fact:
this component sits inside an `EditForm`, whose own body also declares the implicit `context`
name, so the Razor compiler asks for a `Context="..."` on one of the two, whether or not either
body ever reads the parameter. Put it on the validator — `Context="formidable"` in the example
below and on the `/attach` sample — and the content can then read `formidable.Engine` inline
where it wants to.

One further gap needs markup rather than a different call: `FormidableValidator` renders no
`<form>` element of its own — it attaches to whatever `EditForm` the page already owns — so it has
nowhere to put the model-level gate id automatically. A page in attach mode still wants the all-suppressed
defensive gate's summary entry to land somewhere, so it renders that id itself, on the `EditForm`
it already has:

```razor
<EditForm Model="_model" OnSubmit="HandleSubmit" id="@GateId" tabindex="-1"
          aria-describedby="@GateMessagesId">
    <FormidableValidator TModel="Order" @ref="_validator" Context="formidable">
        <FormidableModelMessage />
        ...
    </FormidableValidator>
</EditForm>
```

```csharp
private string GateId => FormidableFieldId.For(new FieldIdentifier(_model, string.Empty));

private string GateMessagesId =>
    FormidableFieldId.MessagesFor(new FieldIdentifier(_model, string.Empty));
```

This is the same pattern every `FormidableForm`-rooted page rendered by hand before the form took
it over; attach mode is the one place it still applies. The `aria-describedby` is the second
half of it, and it is owed only where the page renders a
[`FormidableModelMessage`](#formidablemodelmessage): `FormidableForm` points its own `<form>` at
that list's id automatically, and `FormidableValidator` has nowhere to put the same attribute,
exactly as it has nowhere to put the id. Compute that id rather than appending `-messages` to
`GateId` by hand — `FormidableFieldId.MessagesFor` owns the suffix, so a hand-wired reference and
the list it describes cannot drift apart. Leave the component out and leave the attribute off
with it: an id naming nothing is inert to a screen reader, but it is what a scanner reports (see
[CSS and accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby)).

`novalidate` is the page's to write for the same reason. `FormidableForm` renders it by default so
that the browser's own interactive constraint validation never answers a submit ahead of
FluentValidation (see [above](#formidableformtmodel)); `FormidableValidator` reaches no `<form>`
element, so a page that wants the same guarantee writes the attribute on the `EditForm` it owns,
beside the gate id above. An attribute `EditForm` does not recognise lands on the `<form>` it
renders, which is how the id gets there too. Leaving it off is a real choice rather than an
oversight: a page that wants the browser's own constraint UI keeps it by saying nothing.

A field left as a plain `InputText` — the shape a migrating page has most of — owes the same
hand-wiring here as under `FormidableForm`, and attach mode changes none of it: a
`FormidableFieldAnchor` for submit-time disclosure, the field's id for focus, and
`aria-required` and `aria-describedby` for the screen reader, none of which a control with no
field context can ask for. [CSS and
accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby) says what each is
read from, and why the last one is conditional on the message element existing.
[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) wires the set around one field
and holds the id back until a focus misses, so its `FocusFallback` has a miss to recover.

Attach mode gives up nothing on noticing the page change. `FormidableValidator` reconciles
the rendered field set the same way `FormidableForm` does: removing a row prunes its live issues
and arms a reconciling refresh, through a registry signal the component defers past the render
batch that caused it — nothing asked of the code that removed the row.
`NotifyFieldSetChanged()` overrides that timing for a case the built-in signal doesn't reach in
time — for example, reading `Engine` synchronously right after a mutation, ahead of the automatic
reconcile's own continuation. Ordinary use never calls it. See
[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) for the whole story live.

Attach mode gives up nothing on the server round trip, either. `FormidableValidator` exposes the
same `Engine` property, typed as `IFormidableEngine`, and forwards both `ApplyServerIssues`
overloads itself — the sequence of issues, and the deserialized `FormidableValidationProblem` an
HTTP 400 arrives in:

```csharp
var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
if (problem is not null)
{
    _validator!.ApplyServerIssues(problem);
}
```

Reading that body is the same job here as anywhere, and it wants the same guard: a 400 can come
from a proxy or a gateway rather than from the endpoint, and what those send is no verdict, often
not JSON at all. [Server integration](server-integration.md#reading-the-rejection-body) has it in
full.

So the applying is the same single call here as under `FormidableForm`, with no reaching
through `Engine` to get at it, and the same contract applies either way: each apply replaces the
previous server verdict, every issue lands at the severity it carries, and applying any of them
sets `HasSubmitted`, since a server response is treated as a submit result (see
[Server integration](server-integration.md)). `DiscloseLoadedValuesAsync` is forwarded on the same
terms, with the contract [above](#saying-what-loaded-values-have-earned) whole, so a form attached
to someone else's `EditForm` can open on a saved draft saying what it already knows. Capture the
component with `@ref`, and call from the renderer's synchronization context. The engine exists
from the moment the component binds to its cascaded `EditContext`, so a call that beats the first
render throws a message saying exactly that rather than surfacing as a null reference from inside
the component.

## `FormidableCollectionMessage<TValue>`

A `List<T>` property can carry its own rule — `RuleFor(x => x.Items).NotEmpty()` — with no single
control anywhere in the form to register the path that rule reports against.
`FormidableCollectionMessage` is `FormidableFieldMessage`'s collection-level sibling for exactly
that gap: identical rendering, but its override of the registration hook shown above makes it its
own registration:

```csharp
/// <summary>
/// Renders a collection-level field's current validation issues (any severity) as an accessible
/// message list — identical rendering to <see cref="FormidableFieldMessage{TValue}"/>, so the
/// list itself renders always, empty when the field has no issues — and additionally registers
/// the field with the field registry, so a collection-level rule's issues are treated as revealed
/// even though the collection itself (e.g. a <c>List&lt;T&gt;</c> property) has no validated
/// input of its own to register it.
/// </summary>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableCollectionMessage<TValue> : FormidableMessageBase<TValue>
{
    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    private protected override FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) =>
        context.Registry.Register(field, KeepRegistered);
}
```

*Source: `src/Formidable.Blazor/FormidableCollectionMessage.cs`*

`FormidableCollectionMessage` needs no pairing with anything else — it is its own registration,
because a `List<T>` property with a collection-level rule otherwise has no rendered input to
register the path at all. See [Collections and row identity](collections-and-row-identity.md) for
the nested-collection pattern this exists for.

## `FormidableField<TValue>` and `FormidableFieldContext`

Not every control belongs to Formidable's own kit: a UI library's own `<select>`, a checkbox
group, a third-party date-picker widget. `FormidableField` is the any-UI-library integration
point for those. It is a renderless component that registers its field and, on every render,
hands its `ChildContent` a fresh `FormidableFieldContext` — state, issues, computed CSS class,
the aria ids, and what the submit profile's rules demand of the field — instead of rendering any
markup of its own:

```csharp
public sealed class FormidableFieldContext
{
    private readonly IFormidableEngine _engine;

    internal FormidableFieldContext(
        IFormidableEngine engine,
        FieldIdentifier field,
        string elementId,
        FieldState state,
        string cssClass,
        IReadOnlyList<ValidationIssue> issues)
    {
        _engine = engine;
        Field = field;
        ElementId = elementId;
        State = state;
        CssClass = cssClass;
        Issues = issues;
        AriaInvalid = state.HasErrors;
        AriaDescribedBy = issues.Count > 0 ? FormidableFieldId.MessagesFor(elementId) : null;
        Requirement = engine.GetFieldRequirement(field);

        var inputAttributes = new Dictionary<string, object>(5)
        {
            ["id"] = elementId,
            ["class"] = cssClass,
        };
        if (AriaInvalid)
        {
            inputAttributes["aria-invalid"] = "true";
        }
        if (AriaDescribedBy is not null)
        {
            inputAttributes["aria-describedby"] = AriaDescribedBy;
        }
        if (Requirement == FieldRequirement.Required)
        {
            inputAttributes["aria-required"] = "true";
        }
        InputAttributes = inputAttributes;
    }

    /// <summary>The field this context describes.</summary>
    public FieldIdentifier Field { get; }

    /// <summary>The deterministic element id for the field's input (see <see cref="FormidableFieldId"/>).</summary>
    public string ElementId { get; }

    /// <summary>The field's current state (touched, modified, validating, errors, warnings).</summary>
    public FieldState State { get; }

    /// <summary>The computed CSS class string for the field's current state (see <see cref="FormidableCss"/>).</summary>
    public string CssClass { get; }

    /// <summary>The field's current issues, any severity.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>True when the field currently has error-severity issues — bind to the input's <c>aria-invalid</c>.</summary>
    public bool AriaInvalid { get; }

    /// <summary>
    /// The id of the element holding the field's messages, or null when it has none — bind to
    /// the input's <c>aria-describedby</c>. It is
    /// <see cref="FormidableFieldId.MessagesFor(Microsoft.AspNetCore.Components.Forms.FieldIdentifier)"/>
    /// for <see cref="Field"/>, the same id the field's message list renders on itself.
    /// Deliberately a single id, never a merged list: the consumer composes the markup here, so
    /// a control that also carries its own hint writes
    /// <c>aria-describedby="@($"my-hint {field.AriaDescribedBy}")"</c> itself — the same
    /// splatted-first, messages-id-after order the kit's inputs merge a splatted
    /// <c>aria-describedby</c> in.
    /// </summary>
    public string? AriaDescribedBy { get; }

    /// <summary>
    /// How firmly the submit profile's rules demand that the field carry a value — see
    /// <see cref="IFormidableEngine.GetFieldRequirement"/> for where the answer comes from
    /// and what it cannot see. <see cref="FieldRequirement.Required"/> is what
    /// <c>FormidableRequiredIndicator</c> marks and what puts <c>aria-required</c> in
    /// <see cref="InputAttributes"/>; a control rendering its own marker reads all three values
    /// here and decides for itself, which is the only way to draw anything for
    /// <see cref="FieldRequirement.ConditionallyRequired"/>.
    /// </summary>
    public FieldRequirement Requirement { get; }

    /// <summary>
    /// The one-splat seam for a foreign control: <c>id</c>, <c>class</c>, and — only when
    /// applicable — <c>aria-invalid</c>, <c>aria-describedby</c> and <c>aria-required</c>,
    /// bundled exactly as <see cref="ElementId"/>, <see cref="CssClass"/>,
    /// <see cref="AriaInvalid"/>, <see cref="AriaDescribedBy"/> and <see cref="Requirement"/>
    /// already report them. Splat it onto the control with
    /// <c>@attributes="field.InputAttributes"</c>; <see cref="NotifyChanged"/> is still the
    /// consumer's own wiring, since only the consumer's markup knows which native event commits
    /// the control's value.
    /// </summary>
    public IReadOnlyDictionary<string, object> InputAttributes { get; }

    /// <summary>
    /// Notifies the EditContext that the field changed, which is what marks it touched and runs the
    /// engine's live validation pass — call from a custom input's change handler. Calling it is the
    /// consumer's statement that a committed value change happened: it engages the field, and every
    /// subsequent live pass answers an engaged field's verdict, not only the pass this call
    /// triggers.
    /// </summary>
    public void NotifyChanged() => _engine.EditContext.NotifyFieldChanged(Field);

    /// <summary>Marks the field touched without notifying a value change — call from a custom input's blur/focus-out handler.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);
}
```

*Source: `src/Formidable.Blazor/FormidableFieldContext.cs`*

Everything a hand-rolled control needs is on that context: `ElementId` for the id to render,
`CssClass` for the same state class a Formidable input would compute, `AriaInvalid` and
`AriaDescribedBy` for the same `aria-invalid` and `aria-describedby` an input renders,
`Requirement` for what the submit profile demands of the field — the answer `aria-required`
follows — `InputAttributes` to splat every one of them in one go, and
`NotifyChanged()`/`MarkTouched()` to drive the engine the way a Formidable input's own change
handler does internally. The worked example — wrapping a plain `<select>`, including how to
label it correctly — is one of the seams below, in
[The foreign-control pattern](#the-foreign-control-pattern).

### Naming a field whose type your own component doesn't know

`For` is `Expression<Func<TValue>>` and `TValue` is inferred from the accessor, so a shared
component of your own that has to name a field of any type declares its parameter
`Expression<Func<object>>` and forwards it:

```razor
@using System.Linq.Expressions

<label>@Label <FormidableRequiredIndicator For="For" /></label>
<FormidableFieldMessage For="For" />

@code {
    [Parameter, EditorRequired]
    public Expression<Func<object>> For { get; set; } = default!;

    [Parameter]
    public string? Label { get; set; }
}
```

That is supported, and the call site writes the accessor exactly as it would for a typed `For`:
`<MyField For="() => _order.Description" />`. It works on all five components whose `TValue`
comes from `For` alone — `FormidableField`, `FormidableFieldMessage`,
`FormidableCollectionMessage`, `FormidableFieldAnchor` and `FormidableRequiredIndicator` — each
of which resolves the accessor through `FieldIdentifier.Create`, which reads past the boxing
conversion the compiler inserts when the member is a value type and lands on the field a typed
`For` names. The typed inputs are the exception, and not by omission: an input's `TValue` is the
type it binds, so `FormidableInputText` is a `FormidableInputBase<string?>` and its `For` is an
`Expression<Func<string?>>` and nothing else.

## `FormidableFieldAnchor<TValue>`

A registration-only marker for a field rendered by markup Formidable doesn't wrap and that isn't
using `FormidableField` either — a raw `<input>`, a native `<select>` bound manually, a
third-party component. It renders nothing:

```csharp
public sealed class FormidableFieldAnchor<TValue> : FormidableAccessorComponentBase<TValue>
{
    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>
    /// False: an anchor renders nothing, so a validation state change gives it nothing to
    /// re-render — registering the field is its whole job.
    /// </summary>
    protected override bool ObservesEngineState => false;

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.Register(ResolveField(), KeepRegistered);
}
```

*Source: `src/Formidable.Blazor/FormidableFieldAnchor.cs`*

The whole component is its registration: the shared base does the binding, and an anchor is the one
component that opts out of the engine subscription, since it has no markup of its own to re-render.

Without something registering a field, a submit's verdict about it is never disclosed, and placing
a `FormidableFieldAnchor` next to the raw control is the whole fix. See the Vanilla interop section
below and [Disclosure](disclosure.md) for the fuller "FormidableFieldAnchor for raw and foreign
controls" treatment.

`FieldRegistry.Register` is public, and calling it yourself is supported for the case the anchor
cannot express: a field you already hold as a `FieldIdentifier` rather than as an accessor
expression. `FormidableFormContext.Registry` is the route to it, the `FieldRegistration` it
returns is the handle, and disposing that handle unregisters — unless the call passed
`keepRegistered: true`, the virtualized-row case, which leaves the field registered after the
handle is disposed. What it buys is what the anchor buys: the field counts as rendered, so the
submit channel stops suppressing its errors as unrevealed, and under
`LiveIssueDisclosure.EngagedAndVisible` the live channel stops filtering them too. It also records
the field as having registered at all, which is what makes it eligible for the engagement prune
once it leaves, and what quiets `NeverRegisteredFieldDiagnostic` for it. What the anchor also does,
and a bare call does not, is the lifecycle around it —
`FormidableComponentBase` takes a new registration whenever the cascaded context instance is
replaced, and a host that swaps its model rebuilds engine and registry together, so a handle held
across that swap belongs to a registry nothing consults any more. A component calling the
registry directly owns that re-registration, and owns rendering the field's
`FormidableFieldId.For` id if a summary click is to reach the control.

## The foreign-control pattern

`FormidableField`'s sample page wraps a plain `<select>` — a control Formidable does not, and
cannot know how to, wrap itself:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid">
    <div class="summary-slot">
        <FormidableSummary />
    </div>

    <FormidableField For="() => _order.Colour" Context="field">
        <div class="field">
            <label for="@field.ElementId">Colour</label>
            <select @attributes="field.InputAttributes"
                    value="@_order.Colour" @onchange="args => OnColourChanged(args, field)">
                <option value="">Choose…</option>
                <option>Red</option>
                <option>Green</option>
                <option>Blue</option>
            </select>
        </div>
        <FormidableFieldMessage For="() => _order.Colour" />
    </FormidableField>

    <div class="actions">
        <button type="submit">Submit</button>
    </div>
</FormidableForm>
```

*Excerpt from `samples/Formidable.Sample/Pages/ForeignControl.razor`* — the page also carries a
teaching panel above the form.

The page uses `<label for="@field.ElementId">` rather than wrapping the control in a label,
because the label has to target the foreign element's own id — and only the field context knows
it. The change handler lives in the code-behind:

```csharp
    private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
    {
        _order.Colour = args.Value?.ToString() ?? string.Empty;
        field.NotifyChanged();
    }
```

*Excerpt from `samples/Formidable.Sample/Pages/ForeignControl.razor.cs`*

`field.NotifyChanged()` in the change handler is doing exactly what the base's own `NotifyChanged`
does for `FormidableInputBase` descendants — mark touched, notify the `EditContext` — just called
explicitly instead of being baked into a base class, because there is no base class here to bake
it into.

## `FocusFallback`

`FormidableSummary`'s click-to-focus targets elements in the DOM. `FocusAsync` locates the target by
DOM id (see [CSS and accessibility](css-and-accessibility.md) for the focus service's
mechanism) and answers whether that element took focus, so a click reaches only an element that is
rendered right now and willing to take it. A field
whose row sits outside a `Virtualize` container's current render window keeps its summary entry
(the issue is genuinely still there) but has no DOM element yet for the button to focus; a field
inside a closed `<details>` has one that refuses. `FocusFallback` is the escape hatch for both:

```csharp
    /// <summary>
    /// Invoked when a clicked issue's element does not take focus (focus miss) — nothing on the
    /// page carries the field's id, as for a virtualized row outside the render window, or the
    /// element that carries it will not take focus, as one inside a collapsed section will not.
    /// Return <c>true</c> after making the element reachable (scrolling its container, expanding
    /// that section) and the summary retries the focus exactly once; return <c>false</c> to leave
    /// the miss as-is. When unset, a miss is silently ignored, matching the component's
    /// pre-fallback behaviour.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

Give `FormidableSummary` a `FocusFallback` for controls it might miss. The callback receives the field
identifier on a focus miss: make the element reachable (scroll the virtualized container to the
row's offset, open the section around it, enable the control), return `true`, and the summary
retries the focus once.
[Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) below walks the sample's own
fallback end to end — its fallback scrolls by approximate row height and lets the retried focus
centre the row exactly. A fixed post-scroll delay keeps the sample honest and simple; a
production consumer might poll for the element instead.

`FormidableForm` has the identical gap and the identical seam: every focus move the form itself
makes (see [above](#formidableformtmodel)) can miss the same way a summary click can, and its
`FocusFallback` parameter recovers it the same way:

```csharp
    /// <summary>
    /// Invoked once when the first error one of this form's own focus moves aimed at does not
    /// take focus: no element renders its id, as for a virtualized row outside the render window,
    /// or the element that does will not take focus, as one inside a collapsed section will not.
    /// Return <c>true</c> after making the element reachable (scrolling its container, expanding
    /// that section) and the focus is retried exactly once; return <c>false</c> to leave the miss
    /// as-is. Same delegate shape as <see cref="FormidableSummary.FocusFallback"/> — a page
    /// wiring both typically passes the same callback to each. When unset, a miss reports a
    /// diagnostic instead of the summary's silent default: those moves have nowhere else for the
    /// visitor to land, where the summary's own click just leaves the click without effect.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

Same name, same delegate type, same try-fallback-retry-once shape — a page that already wrote a
fallback for its summary hands the identical method to the form. `/workout` does exactly that: the
same `RecoverMissedFocusAsync` that recovers a summary click for an off-screen session row also
recovers the form's own auto-focus on a blocked submit, so a visitor who never clicks the summary
at all still lands in the row that failed. The one place the two callers diverge is what happens
with nothing wired: the summary stays silent (a miss just leaves the click without effect), but the
form reports a diagnostic, because the moves a form makes have nowhere else for the visitor to
land. That covers all three of them — the blocked submit, the one either `ApplyServerIssues`
overload makes for a rejected round trip, and the one a page asks for with `FocusFirstErrorAsync()`
— since none of them was a click that could simply have no effect.

## `PrepareFocus`

`FocusFallback` recovers a move that has already missed. A field can be unreachable without ever
producing one: a modal dialog announcing the blocked submit sits over the form, its overlay
covering the field while leaving it perfectly focusable, so `FocusAsync` moves focus to it behind
the overlay, reports success, and no fallback fires. The caret is now in a box the visitor cannot
see. `PrepareFocus` is the seam that runs first, so the page can clear the way before a move is
attempted at all. It is the better seam even where a miss would be reported — a collapsed section
around the field, a hidden tab panel holding it — since clearing the way before the attempt beats
recovering after it.

`FormidableForm`, `FormidableValidator` and `FormidableSummary` all take it, with the same
delegate shape on each, so one page callback wires to all three exactly as one `FocusFallback`
does. The delegate returns `ValueTask` rather than `ValueTask<bool>` because there is no verdict
to give: the kit awaits it and then runs the usual try, fall back once, retry pipeline unchanged
behind it.

**A dialog opened from `OnInvalidSubmit` has to
[suppress the form's own move](#suppressing-the-automatic-focus).** That move runs immediately
after the handler returns, inside the same submit call, so left alone it happens the moment the
dialog opens and lands behind the overlay. Worse, wire a dismissing `PrepareFocus` to the form as
well and that move runs the callback, closing the dialog the instant it appeared. With the move
suppressed, the moves left are the ones the visitor asks for, and clicking an entry is the one
this hook is there to prepare:

```razor
<FormidableForm Model="_order" OnValidSubmit="Save" OnInvalidSubmit="AnnounceAsync">

    @* the form's own fields and buttons *@

    <AnnouncementDialog @ref="_announcement">
        <FormidableSummary PrepareFocus="DismissAnnouncementAsync" />
    </AnnouncementDialog>
</FormidableForm>
```

```csharp
    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        context.SuppressFirstErrorFocus();
        await _announcement!.OpenAsync();
    }

    private async ValueTask DismissAnnouncementAsync(FieldIdentifier field)
    {
        if (_announcement is not null)
        {
            await _announcement.CloseAsync();
        }
    }
```

`AnnouncementDialog` is the page's own component; the two nestings in that markup are both
load-bearing. The summary sits inside the dialog so the entries the visitor can click are the ones
in front of the overlay rather than behind it. The dialog sits inside the form because
`FormidableSummary` reads the cascaded form context and throws when it is rendered outside a
`FormidableForm` or `FormidableValidator` ancestor.

**Complete when the target is genuinely reachable, not when it has started becoming reachable.**
A dialog does not disappear on the state change that closes it. There is a transition to finish,
an overlay and any scroll lock to remove, and a focus restoration handing focus back to whatever
opened the dialog. The restoration is what takes a premature move straight back; the transition
and the overlay are why a field focused ahead of them is not yet one the visitor can use. Await
the dialog component's own closed event, and the move lands where it was meant to.

It runs once per move, ahead of the first attempt: a `FocusFallback` retry does not run it a
second time, since the page already cleared the way for this move. It runs only when a move is
actually about to be made, too. A host that registered no `IFormidableFocusService`, a move that
finds no visible issue to land on, and a blocked submit whose move
`FocusFirstErrorOnInvalidSubmit="false"` or a suppressing handler called off each leave it unrun,
so a side effect as visible as closing a dialog is never paid for a focus that was never going to
happen. That last case is the dialog shape above, where calling the root's move off leaves the
summary's own hook as the only `PrepareFocus` still doing work.

Exceptions are handled the way the path already handles a throwing `FocusFallback`, which leaves
one rule to learn rather than a second. A throw surfaces out of whichever call asked for the
move — `SubmitAsync`, `ValidateForSubmitAsync`, `FocusFirstErrorAsync` on either root (or on the
cascaded context, which calls the root's), or a summary entry's click. It reaches no caller of
either `ApplyServerIssues` overload and is left to become an unobserved task exception: applying
server issues is synchronous by contract, so its focus move is fire-and-forget and there is no
caller left holding it.

[`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) runs the whole shape, and
carries a toggle for each of the two things that break it: the form's own move left unsuppressed,
and a dismissal that reports ready before the dialog has finished closing.

## `AddFormidableBlazor()`

The one-call registration for a Blazor client — everything `AddFormidable()` registers (see
[Server integration](server-integration.md) for the server-side registration this
mirrors) plus the three JS-backed services: focus, DOM value sync, and field order:

```csharp
    /// <summary>
    /// Registers Formidable's core services (see <see cref="FormidableServiceCollectionExtensions.AddFormidable"/>)
    /// plus <see cref="IFormidableFocusService"/>, <see cref="IFormidableDomValueSync"/> and
    /// <see cref="IFormidableFieldOrderService"/>. The one-call registration for Blazor consumers.
    /// Existing registrations are respected.
    /// </summary>
    public static IServiceCollection AddFormidableBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFormidable();
        services.TryAddScoped<IFormidableFocusService, FormidableFocusService>();
        services.TryAddScoped<IFormidableDomValueSync, FormidableDomValueSync>();
        services.TryAddScoped<IFormidableFieldOrderService, FormidableFieldOrderService>();
        return services;
    }
```

*Source: `src/Formidable.Blazor/FormidableBlazorServiceCollectionExtensions.cs`*

`TryAddScoped` means a consumer that has already registered its own `IFormidableFocusService` (a
custom focus/scroll behavior) keeps it — `AddFormidableBlazor()` never overwrites an existing
registration.

That is also what makes all three testable. Each is a public interface over an internal JS-backed
implementation, so a bUnit test registers its own stand-in first and `AddFormidableBlazor()`
leaves it alone: a focus double makes which field a blocked submit moved to assertable, a DOM
value sync double keeps a number or date input off interop entirely, and an
`IFormidableFieldOrderService` double is how a test states a document order without a document
(see [Testing](testing.md#the-form-under-bunit)).

An overload takes an `Action<FormidableOptions>` and registers the configured instance as the
app-wide default, which every form that omits its own `Options` parameter then uses:

```csharp
    /// <summary>
    /// Registers everything <see cref="AddFormidableBlazor(IServiceCollection)"/> does, plus a
    /// <see cref="FormidableOptions"/> singleton configured by <paramref name="configureDefaults"/>.
    /// Every Formidable form that omits its own <c>Options</c> parameter uses that instance, so a
    /// design system's class names or a team's debounce are stated once for the whole app instead
    /// of on every form. A form's own <c>Options</c> parameter still wins where it is passed, and
    /// wins whole: resolution has no merging step, so a form that differs in one setting copies
    /// this instance rather than restating the rest — see
    /// <see cref="FormidableOptions(FormidableOptions)"/>.
    /// Existing registrations are respected.
    /// </summary>
    /// <remarks>
    /// The configured instance is a singleton the whole app shares, and an engine reads each of
    /// its properties at each use — a pass selecting its profile, a timer arming, a render asking
    /// for a class name — so mutating it at runtime changes behaviour in every live form, not just
    /// the one being looked at. Where a property's own remarks state a coarser read, that
    /// governs: <see cref="FormidableOptions.ClickRecovery"/> is read once per root and
    /// <see cref="FormidableOptions.VerifyRowKeys"/> once per bound component, so a change to
    /// either reaches nothing that has already read it.
    /// </remarks>
    public static IServiceCollection AddFormidableBlazor(
        this IServiceCollection services, Action<FormidableOptions> configureDefaults)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDefaults);
        services.AddFormidableBlazor();

        var defaults = new FormidableOptions();
        configureDefaults(defaults);
        services.TryAddSingleton(defaults);
        return services;
    }
```

*Source: `src/Formidable.Blazor/FormidableBlazorServiceCollectionExtensions.cs`*

That is the second step of the three-step options order stated in [Need to
know](#need-to-know): parameter, then this, then `new FormidableOptions()`. A design system's
class names belong here rather than on every page — see [Engine
options](options.md#app-wide-defaults), which works through the copy a form builds when it wants
those defaults and one setting of its own.

## Culture at WebAssembly boot

A WebAssembly app downloads its satellite resource assemblies for whatever culture is current when
`RunAsync()` is called, and never again. An app that lets a visitor choose a language therefore has
to apply the stored choice *before* that line, not from a page afterwards.

Formidable ships nothing for this, deliberately. Every part of it is the app's own decision: where
the choice is kept, under what key, and what a stale one should do. None of that is a validation
library's to freeze. The plumbing is short, and the sample writes it in the open: `Program.cs`
hands a reader to [`CultureBootstrap`](../samples/Formidable.Sample.Shared/CultureBootstrap.cs),
which parses the stored name and sets both `CultureInfo.DefaultThreadCurrentCulture` and
`DefaultThreadCurrentUICulture` to it:

```csharp
var js = host.Services.GetRequiredService<IJSRuntime>();
await CultureBootstrap.ApplyStoredCultureAsync(
    () => js.InvokeAsync<string?>("formidableSample.getCulture").AsTask(),
    CultureInfo.GetCultureInfo("en-AU"));

await host.RunAsync();
```

*Excerpt from `samples/Formidable.Sample/Program.cs`*

A missing, blank or unrecognisable value applies the fallback instead. Pass no fallback and the app
keeps the culture it booted with, so a stale or corrupted stored value cannot stop it from starting.
The reader is a delegate rather than a storage call, which is what keeps the choice of storage the
app's: the sample reads `localStorage` under `formidable.culture`, and a cookie, a user profile
fetched from the server or a canned test value all fit the same shape.

Once the culture is set, FluentValidation's own message translations follow
`CultureInfo.CurrentUICulture` with no further wiring. See [Profiles](profiles.md) for the
localization story and the display-name half of it.

This is a WebAssembly concern only. A Blazor Server host sets culture from the request instead, and
needs no boot-time step at all.

**Sample:** [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor) — stores the
choice and reloads, which is what makes the boot-time read the only place the culture can change.

## Virtualize and `KeepRegistered`

Every registering component exposes a `KeepRegistered` parameter (`FormidableInputBase<TValue>`
descendants, `FormidableFieldAnchor`, `FormidableField`, `FormidableCollectionMessage`). A
`Virtualize` container disposes rows that scroll out of view even though they remain part of the
form. Without `KeepRegistered`, a scrolled-away row's field would unregister and its
already-showing error would go quiet while the row still sits in the model. The sample page
pairs that with `FormidableSummary`'s `FocusFallback` (see above), so a row far outside the
render window is both kept disclosed and reachable by a summary click:

```razor
<FormidableForm Model="_order" Options="_options" OnValidSubmit="HandleValid" FocusFallback="ScrollToRowAsync">
    <div class="summary-slot">
        <FormidableSummary FocusFallback="ScrollToRowAsync" />
    </div>

    <div class="scroll-panel">
        <Virtualize Items="_order.Gadgets" ItemSize="RowHeight" Context="gadget">
            <div class="field" @key="gadget">
                <label>Serial
                    <FormidableInputText @bind-Value="gadget.Serial" KeepRegistered="true" />
                </label>
                <FormidableFieldMessage For="() => gadget.Serial" />
            </div>
        </Virtualize>
    </div>

    <div class="actions"><button type="submit">Submit</button></div>
</FormidableForm>
```

*Excerpt from `samples/Formidable.Sample/Pages/Virtualized.razor`* — the full page wraps this in a
`TeachingPanel` (rules plus a "Show the code" accordion with the real source); the form markup
itself is unchanged from what's shown here.

`KeepRegistered="true"` on the row's `FormidableInputText` keeps a scrolled-away row's error in
`FormidableSummary` no matter how far it scrolls, exactly as before. The sample also sets a
`DisclosureOverride` for the collection, so even rows Virtualize has never rendered keep their
place in the summary — validation always runs against the full model; the override only lifts the
visibility gate. The same `ScrollToRowAsync` callback goes to both `FocusFallback` parameters
above, so a blocked submit's own auto-focus recovers an off-screen row exactly as a summary click
does — no click required to reach the row a submit's first error names. What's new is
`ScrollToRowAsync` itself, the code-behind method wired to both:

```csharp
    private const float RowHeight = 118f;
```

```csharp
    private async ValueTask<bool> ScrollToRowAsync(FieldIdentifier field)
    {
        if (field.Model is not Gadget gadget)
        {
            return false;
        }

        var index = _order.Gadgets.IndexOf(gadget);
        if (index < 0)
        {
            return false;
        }

        await Js.InvokeVoidAsync("formidableSample.scrollPanelTo", ".scroll-panel", index * RowHeight);
        await Task.Delay(120);
        return true;
    }
```

*Excerpt from `samples/Formidable.Sample/Pages/Virtualized.razor.cs`*

Clicking a summary entry for a row inside the current render window still focuses it directly.
For a row scrolled far away, the miss triggers `ScrollToRowAsync`, which scrolls `.scroll-panel`
to the row's approximate offset (`index * RowHeight`) and waits 120ms for `Virtualize` to render
it before returning `true`. The caller then retries the focus, and its own `scrollIntoView`
centres the row exactly. A blocked submit reaches the identical miss the same way: `FormidableForm`
tries the row's element first, falls back to `ScrollToRowAsync` on the same terms as the summary,
and retries — so submitting with the first error on an unrendered row needs no summary click at
all to land there.

## Vanilla interop

Because `FormidableForm` renders a real `EditForm`, plain Blazor form components work inside it
unmodified — a native `InputText` and `ValidationMessage` beside a Formidable input in the same
form:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid" @ref="_form">
    <div class="summary-slot">
        <FormidableSummary />
    </div>

    <div class="field">
        <label>Nickname (native InputText)
            <InputText @bind-Value="_order.Nickname"
                       id="@NicknameId"
                       aria-invalid="@NicknameAriaInvalid"
                       aria-describedby="@NicknameAriaDescribedBy" /></label>
        <ValidationMessage For="() => _order.Nickname" id="@NicknameMessagesId" />
        <FormidableFieldAnchor For="() => _order.Nickname" />
    </div>

    <div class="field">
        <label>Colour (Formidable input)
            <FormidableInputText @bind-Value="_order.Colour" /></label>
        <FormidableFieldMessage For="() => _order.Colour" />
    </div>

    <div class="actions">
        <button type="submit">Submit</button>
    </div>
</FormidableForm>
```

*Excerpt from `samples/Formidable.Sample/Pages/VanillaInterop.razor`* — the page also carries a
teaching panel above the form.

The native `InputText` gets the same state classes a Formidable input would, because the engine
installs Formidable's `FieldCssClassProvider` on the `EditContext` itself at construction — every
`InputBase` descendant in the form picks it up automatically, Formidable-aware or not:

```csharp
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(this));
```

*Source: `src/Formidable.Blazor/FormidableEngine.cs`*

(See [CSS and accessibility](css-and-accessibility.md) for exactly which classes that
provider applies, and how they differ from what a Formidable input's own `CssClass` computes.)

`FormidableFieldAnchor` next to the native `InputText` is what keeps it participating in the
submit channel's disclosure. A plain `InputBase` never registers itself with Formidable's
`FieldRegistry`, so without the anchor no submit reveals `Nickname`, and its submit errors are
suppressed as unrevealed. Its live error lands either way: the first committed change engages the
field, and an engaged field's verdict reaches the store — and so that `ValidationMessage` —
whether or not anything registered it. Opting into
`LiveIssueDisclosure.EngagedAndVisible` is what puts the live channel behind this same
registration, and is the other thing the anchor buys.

The three attributes on that same line finish the crossing. A Formidable input renders
`FormidableFieldId.For(field)` as its element id, points `aria-describedby` at the matching
`-messages` id, and emits `aria-invalid="true"` while the field has errors. A native input has
no field context to take any of that from, so the page supplies all of it from the code-behind:
`NicknameId` and `NicknameMessagesId` compute the two ids, `GetFieldState(field).HasErrors`
answers `aria-invalid`, and `EditContext.GetValidationMessages(field)` decides whether
`aria-describedby` names that messages id at all — a `null` value renders no attribute, and the
native `ValidationMessage` renders no element to be described by while the field is clean. Naming
`aria-invalid` in the markup also settles who answers for it, which is its own question: [CSS and
accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby) has what a Blazor
`InputText` does with a named attribute and with an absent one. Looking the target up is the
id's whole job, and an `<input>` takes focus without further help, so with the id the native
field takes the summary's click exactly like a wrapped one
(see [CSS and accessibility](css-and-accessibility.md)). One addition per concern:
`FormidableFieldAnchor` for submit-time disclosure, the id for focus, `aria-describedby` and
`aria-invalid` for the assistive-technology story. Attributes derived from engine state need
the page to re-render when that state changes, so the sample subscribes to
`Engine.StateChanged` — the same subscription every kit component makes for itself.

**Samples:** [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), and
[`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor) for the CSS merge
(consumer `class` kept, computed state classes remapped onto a UI library's own).
