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
    internal static FormValidationEngine<TModel> Create<TModel>(
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
  comes from the container.
- **The introspector.** `IModelIntrospector` comes from the container or throws. There is no
  parameter for it.
- **The options.** An `Options` parameter wins, then an app-wide default registered through
  [`AddFormidableBlazor(...)`](#addformidableblazor), then `new FormidableOptions()`.

A fourth thing is resolved too, on a simpler rule that has no parameter to win: the engine's
logger comes from `ILoggerFactory` when one is registered, or stays `null` otherwise — see
[`SuppressedIssueDiagnostic`](options.md#suppressedissuediagnostic) for what it's for.

The validator's two failures are worth reading in full, because between them they are how a first
form fails to start:

```csharp
        return resolved ?? throw new InvalidOperationException(
            $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call " +
            "services.AddFormidableBlazor() and register the FluentValidation validator.");
```

```csharp
    private static string MissingFluentValidatorMessage<TModel>()
    {
        var name = FriendlyTypeName.Of(typeof(TModel));
        return $"No FluentValidation validator for '{name}' is registered, so Formidable's " +
            $"IModelValidator<{name}> adapter cannot be constructed. Register one with " +
            $"services.AddScoped<IValidator<{name}>, {name}Validator>(), or register a whole " +
            "assembly's validators at once with services.AddValidatorsFromAssembly().";
    }
```

*Excerpt from `src/Formidable.Blazor/FormidableEngineFactory.cs`*

The first fires when nothing is registered — no `AddFormidableBlazor()` call anywhere. The second
is the far commoner one: Formidable is registered, so the open-generic adapter exists, but the
FluentValidation validator it wraps does not. That state makes the container throw while
*building* the adapter rather than return null, so it gets caught and renamed; the container's own
exception is preserved as the inner one. Both strings are Formidable's, which is what keeps them
readable in a trimmed WebAssembly build.

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
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            if (AdditionalAttributes is not null)
            {
                inner.AddMultipleAttributes(3, AdditionalAttributes!);
            }
            // Rendered after the splat, so they win the duplicate-attribute race: the all-suppressed
            // gate's summary entry addresses the form by this id (see FormidableFieldId), and a
            // consumer-supplied id or tabindex would break that the same way a consumer-supplied
            // input id would — see FormidableInputBase<TValue>'s identical policy.
            inner.AddAttribute(4, "id", _modelLevelFieldId);
            inner.AddAttribute(5, "tabindex", "-1");
            inner.AddComponentParameter(6, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent ?? (_ => { })));
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
    /// the token can still run to completion, so the blocked <see cref="SubmitOutcome"/> below is
    /// returned instead of whatever that pass actually decided.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var engine = RequireEngine();
        var outcome = await engine.ValidateForSubmitAsync();

        if (!ReferenceEquals(_engine, engine))
        {
            // The dead engine's own verdict — even a passing one, if its validator outran
            // cancellation — must not surface as current; mirrors FormValidationEngine's own
            // precedent for a superseded pass with nothing to report.
            return new SubmitOutcome(false, ValidationReport.Empty, []);
        }

        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync(outcome);
        }
        else
        {
            await OnInvalidSubmit.InvokeAsync(outcome);
            if (FocusFirstErrorOnInvalidSubmit)
            {
                await FocusFirstErrorAsync();
            }
        }

        StateHasChanged();
        return outcome;
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

Both branches hand their handler the same payload. `OnValidSubmit` is an
`EventCallback<SubmitOutcome>`, exactly as `OnInvalidSubmit` has always been: a passing submit can
still carry advisories, and a handler that wants them shouldn't have to reach through `Engine` to
find them. A parameterless handler still binds — Blazor's own `EventCallback<TValue>` conversion
accepts an `Action` or a `Func<TResult>` wherever a typed callback is declared, the same way it does
for `EditForm.OnValidSubmit` — which is why `OnValidSubmit="HandleValid"` over a `void
HandleValid()` is still the shape every sample page uses. Take the parameter when the outcome is
what you're after: `CanProceed`, the full `Report`, and `VisibleErrorSummary` for a dialog (see
[Severity](severity.md)).

The engine itself is exposed as a public property: `Engine => _engine`, typed as the non-generic
`IFormValidationEngine`. `FormidableForm` also forwards its two overloads directly
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
issue when there is no error to find at all. A blocked submit reaches that state exactly one way:
superseded by a second submit before its own verdict landed, it reports blocked without writing a
verdict, leaving whatever preceded it on screen. Every other way a submit blocks writes an
error-severity issue (the all-suppressed gate's explanation among them), so the fallback has
nothing else to catch. A validator fault is error-severity too, when a live or refresh pass is
the one reporting it: it never shows up from a submit itself, since `RunPassAsync` catches the
exception only for those two kinds of pass, so a fault during submit propagates to the caller
instead of leaving a blocked verdict behind.
The call is best-effort in both directions a consumer might trip on: an app that never
registered `IFormidableFocusService` (only `AddFormidable()`, not `AddFormidableBlazor()`) gets
silence rather than a resolution failure, and a focus miss — no element carries the field's id
yet — is silent too, exactly like the summary's own click-to-focus.
Set `FocusFirstErrorOnInvalidSubmit="false"` to choose focus yourself from `OnInvalidSubmit`
instead.

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
control of your own to the engine.

What an input contributes to that lifecycle is its `Register`: it resolves the field to a
`FieldIdentifier`, computes the ids that address it, and registers it with the cascaded context's
`FieldRegistry` — which is what makes the field's issues visible to progressive disclosure while
the component stays mounted (see [Disclosure](disclosure.md)):

```csharp
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
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
        CombineClassNames(AdditionalAttributes, FormidableCss.Compute(state, Context!.Engine.Options.CssClasses));
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

```csharp
    private static string CombineClassNames(IReadOnlyDictionary<string, object>? additionalAttributes, string computed)
    {
        if (additionalAttributes is null || !additionalAttributes.TryGetValue("class", out var splatted))
        {
            return computed;
        }

        var splattedClass = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(splattedClass))
        {
            return computed;
        }

        return computed.Length == 0 ? splattedClass : $"{splattedClass} {computed}";
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

A consumer writing `class="form-control"` on a `FormidableInputText` keeps that class and still
gets whichever state class applies — `formidable-invalid`, `formidable-warning`,
`formidable-info`, or `formidable-valid`, plus `formidable-pending` while a pass is in flight —
appended alongside it; the two are merged, never one replacing the other.

**Aria.** `aria-invalid` appears while the field has error-severity issues, and `aria-describedby`
— pointing at the message list's id — while it has any issues at all; a clean field renders
neither.

**Rendering them.** `AddCommonAttributes` is the first of the two calls: it renders the splat,
the id, the class and the aria pair, and the order it renders them in is what the kit's two
consumer-facing guarantees rest on:

```csharp
    protected void AddCommonAttributes(RenderTreeBuilder builder, int sequence)
    {
        var state = State;
        var issues = Context!.Engine.GetIssues(Field);

        builder.AddMultipleAttributes(sequence, AdditionalAttributes!);
        builder.AddAttribute(sequence + 1, "id", ElementId);
        builder.AddAttribute(sequence + 2, "class", ComputeCssClass(state));

        // Both aria attributes share one sequence number: attribute frames diff by name rather than
        // by sequence, and sharing it keeps this call's budget at four numbers for a control
        // numbering its own attributes around it.
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", MessagesElementId);
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

`AdditionalAttributes` enters the render tree first and the computed values after, so the computed
values win the duplicate-attribute race (Blazor applies last-write-wins): that is what merges a
consumer's `class` with the state class and what drops a consumer's `id` in favour of the
deterministic one. Because the order lives in this one call, a derived control gets it by calling
rather than by transcribing. The call consumes four sequence numbers — `sequence` through
`sequence + 3` — so the control's own attributes start at `sequence + 4`. It also reads the field's
state and issues once each per render, and answers both the class and the aria attributes from that
one read.

**Value binding.** `AddValueBinding` is the call a concrete input's `BuildRenderTree` makes,
immediately before closing its element, to wire the attribute(s) that commit a value change —
honouring `UpdateOn` for every mode the enum has, present and future, rather than each input
re-deciding which DOM event to bind:

```csharp
    protected void AddValueBinding(RenderTreeBuilder builder, int sequence)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(sequence, "onchange", EventCallback.Factory.CreateBinder<TValue?>(this, v => CommitValueAsync(v), Value));
            builder.SetUpdatesAttributeName("value");
            AddBlurBinding(builder, sequence + 1);
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TValue?>(this, v => SetCurrentValueAsync(v), Value));
        builder.SetUpdatesAttributeName("value");

        if (SyncsDomValueOnBlur)
        {
            AddBlurBinding(builder, sequence + 1);
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

The trailing branch is the number and date inputs' opt-in: those two controls bind `blur` in
every mode, because their native elements can display text they report as empty and only a
blur-time write can reconcile the box with the model — the mechanism their own sections below
walk through.

Under `OnChange` (default) and `OnInput`, a single event both commits the value and starts
validation — that's `SetCurrentValueAsync`, which assigns `Value`, invokes `ValueChanged`, marks
the field touched, and notifies the `EditContext` so the engine's live validation pass runs, all
in one call:

```csharp
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        await CommitValueAsync(value);
        NotifyChanged();
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

Under `OnBlur`, `AddValueBinding` calls the same two steps apart instead: `CommitValueAsync` alone
on `change` (assigns `Value`, invokes `ValueChanged` — no touch, no notify), then `NotifyChanged`
alone on `blur` (marks the field touched and notifies the `EditContext` — the same two things
`FormidableFieldContext.NotifyChanged` does for a foreign control with no base class to call it
from; see [the foreign-control pattern](#the-foreign-control-pattern) below). Neither half is
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
consumer's own `@onblur` runs first and is awaited, then the engine is notified. Wanting the
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
  renders a verdict freezes that verdict — the state class and the aria pair keep whatever values
  the last render happened to give them. `OnEngineStateChanged` is the relay itself: override it to
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
value committed. `OnBlur` still commits on that same `change` event, but the notification defers
to `blur` instead — the same commit/notify split every other kit input gives that mode, riding the
same `onblur` chaining.

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
                $"{typeof(FormidableInputSelect<TValue>)} does not support the type '{typeof(TValue)}'.", ex);
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
`UpdateOn="OnBlur"` so picking an option commits it at once but the message waits until the
control is actually left.

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
                $"{typeof(FormidableInputNumber<TValue>)} does not support the type '{typeof(TValue)}'. " +
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
a native `stepMismatch`. `FormidableForm` renders no `novalidate` (neither does the framework's
own `EditForm` underneath it), so a real `<button type="submit">` against a `stepMismatch` field
never reaches Blazor's submit handler at all — the browser blocks the submit event and shows its
own constraint-validation tooltip, not FluentValidation's message. The default `step="any"` turns
that native check off, so every fractional value reaches the model and only FluentValidation
judges it, matching native `InputNumber<TValue>`'s own default for every one of its supported
types. Rendering it before the splat, rather than forcing it the way `type` is forced, means a
consumer's own splatted `step` overrides the default outright — and opts back into the browser's
native constraint UI for values that mismatch it, the same trade a consumer accepts by splatting
any other native constraint attribute.

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
the field uncommitted: the model stays what it was. The box itself is squared with the model on
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
engine is notified only once, on `blur`, once the value has had a chance to settle — the same
per-segment problem [Options](options.md#updateon-per-input-not-a-formidableoptions-property)
covers for the general case, now answered by the typed input directly rather than by splatting
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
way the sample transitions [`FormidableSummary`](#formidablesummary) through its own persistent
wrapper (`FormidableSummary` itself renders nothing at all when there are no visible issues,
unlike this list element's always-rendered root), and so a configured `InlineMessageRole` sits on
an element that persists across renders rather than one that enters alongside the text it
announces (see [Options](options.md#inlinemessagerole)). It shares one base
(`FormidableMessageBase<TValue>`) with its collection-level sibling
for resolving `For`, subscribing to the engine's `StateChanged`, and rendering that same list.
That base is public only because a public component cannot inherit a less accessible base; its
constructor is not, so these two components are the only shapes it takes. Unlike
`FormidableInputBase<TValue>`, it is not an extension point.

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
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FormidableMessageBase{TValue}.For"/>).</typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

Practically: `FormidableFieldMessage` always needs to be paired with something else that registers
the same field — a `FormidableInputBase` descendant, `FormidableField`, or `FormidableFieldAnchor`
— or its messages stay permanently unrevealed. Its collection-level sibling,
`FormidableCollectionMessage`, overrides that same hook to skip the pairing requirement entirely;
it's introduced below, once collections are in scope.

## `FormidableSummary`

Renders a live, severity-grouped list of every currently-visible issue across the form — nothing
while the form has no visible issues. The region's `role` is severity-aware: `alert` when any
visible issue is error-severity, the politer `status` when the visible issues are advisories only:

```csharp
        var hasError = visibleIssues.Any(v => v.Issue.Severity == ValidationSeverity.Error);
```

```csharp
        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", hasError ? "alert" : "status");
```

*Excerpt from `src/Formidable.Blazor/FormidableSummary.cs`* — elided in between is the heading-tag
computation driven by the `HeadingLevel` parameter, unrelated to the role wiring shown here.

Each item is a button that moves focus to the offending field through `IFormidableFocusService`,
via the component's own `FocusWithFallbackAsync`:

```csharp
                builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, () => FocusWithFallbackAsync(visibleIssue.Field)));
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

Click-to-focus can only reach an element that's actually rendered — a row scrolled out of a
virtualized container's window, for instance, has no DOM element yet to focus even though its
summary entry is genuinely still there. `FocusFallback` is the escape hatch for that gap,
covered once the seams that need it are in view — see [FocusFallback](#focusfallback) below.

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
narrow it, `Advisories` meaning warnings and infos together, exactly as
`ValidationReport.Advisories` does. A page that wants the blocking problems apart from the
commentary renders two:

```razor
<FormidableSummary Show="SummaryFilter.Errors" />

<fieldset>
    ...
</fieldset>

<p>Worth a look before you send this:</p>
<FormidableSummary Show="SummaryFilter.Advisories" />
```

A filter that matches nothing renders nothing, the same as a clean form — so the advisory summary
disappears on its own while there is nothing to say, and the heading above it is the page's to
hide alongside it.

Each summary reads the same visible issues and filters them independently, which has two
consequences worth planning around. A default summary rendered alongside a filtered one shows
those issues twice — nothing coordinates between them, so pick one shape per form. And each
computes its own `role` from what it actually shows, so two summaries are two live regions and two
announcements: an `Errors` summary announces as `alert` whenever it has anything, an `Advisories`
one always as the politer `status` (see
[CSS and accessibility](css-and-accessibility.md#formidablesummary-as-a-live-region)).

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — two disjoint
summaries stacked one above the other at the top of the form, so what blocks and what merely
advises arrive as separate blocks, and the one with nothing to say is absent rather than empty.

### Heading each band

`ErrorsHeading`, `WarningsHeading` and `InfosHeading` (all `string?`, all `null` by default) give a
severity band its own heading. Set one and the summary renders it as an `h{HeadingLevel}` (`2` by
default, any of `1`–`6`; anything else throws from `OnParametersSet`) inside that band, with an id
this component mints and wires to the band's `<ul>` via `aria-labelledby` — the relationship is the
summary's to get right, not a consumer's to reconstruct. Leave a heading unset and neither the
element nor the attribute appears, so a summary with none of the three set renders exactly as one
that never mentions them. The library ships no user-facing text of its own, so there is no shipped
English default here either: the string is a consumer's own, localized like every other piece of
copy Formidable never writes.

The band itself (`<div class="formidable-summary__band formidable-summary__band--{severity}">`,
wrapping the heading, if any, and the `<ul>` together) renders for every band regardless of
whether that band carries a heading, so a stylesheet has one consistent shape to target rather than
a wrapper that appears only once a heading is set. That wrapper sits between `.formidable-summary`
and each severity's `<ul>`, which is a real markup change for anyone styling past it: a
`.formidable-summary > ul` selector stops matching. What it buys is a panel per severity to style
as one — background, left rule, radius, padding on `.formidable-summary__band` rather than spread
across the `<ul>` and its `formidable-summary__group` class. The sample styles it exactly that
way; where a consumer's own panel styling lives is theirs to decide, since Formidable ships no CSS
of its own (see [CSS and accessibility](css-and-accessibility.md)).

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — the errors
summary carries `ErrorsHeading`, and the advisories summary beside it carries both
`WarningsHeading` and `InfosHeading` for its two bands.

## `FormidableValidator<TModel>`, attaching to an existing form

A form that must attach to an `EditForm` it doesn't own — an existing page already built around a
plain `EditForm`/`EditContext` — doesn't need to give that up to use Formidable. It has an
alternative root, `FormidableValidator<TModel>`, for exactly that case; the full pattern,
including when to reach for it over `FormidableForm`, is covered in
[Migration guide](migration-guide.md).

Attach mode owns the engine, not the form, so the verbs that come with owning an `EditForm` stay
with `FormidableForm`: `SubmitAsync`, `ResetAsync`, `FocusFirstErrorOnInvalidSubmit`, the typed
submit callbacks and the `Model` parameter whose swap rebuilds everything all belong to the
component that renders the `<form>`. A page in attach mode keeps its own `EditForm`'s handlers for
that half. Two behaviours follow focus for the same reason: nothing resolves where the fields
sit, so a summary here lists issues in the engine's own channel order rather than the page's, and
an `ApplyServerIssues` here stays quiet where the form's moves focus. Both are the consequence of
not rendering the `<form>` — see
[Migration guide](migration-guide.md#what-to-check-after-migrating).

`FormidableValidator` cascades its `FormidableFormContext` only to its own `ChildContent` — pass
none and it renders nothing at all, cascade included, rather than an empty wrapper. Everything
that needs the cascade, `FormidableSummary` and any `FormidableInputText`/`FormidableFieldMessage`
pair among them, belongs nested inside `<FormidableValidator>...</FormidableValidator>`, not
sitting beside it as a sibling.

One further gap needs markup rather than a different call: `FormidableValidator` renders no
`<form>` element of its own — it attaches to whatever `EditForm` the page already owns — so it has
nowhere to put the model-level gate id automatically. A page in attach mode still wants the all-suppressed
defensive gate's summary entry to land somewhere, so it renders that id itself, on the `EditForm`
it already has:

```razor
<EditForm Model="_model" OnValidSubmit="HandleValid" id="@GateId" tabindex="-1">
    <FormidableValidator TModel="Order">
        ...
    </FormidableValidator>
</EditForm>
```

```csharp
private string GateId => FormidableFieldId.For(new FieldIdentifier(_model, string.Empty));
```

This is the same pattern every `FormidableForm`-rooted page rendered by hand before the form took
it over; attach mode is the one place it still applies.

Attach mode gives up nothing on noticing the page change. `FormidableValidator` reconciles
the rendered field set the same way `FormidableForm` does: removing a row prunes its live issues,
and a form that has already submitted gets a reconciling refresh, through a registry signal the
component defers past the render batch that caused it — nothing asked of the code that removed the
row. `NotifyFieldSetChanged()` overrides that timing for a case the built-in signal doesn't reach
in time — for example, reading `Engine` synchronously right after a mutation, ahead of the
automatic reconcile's own continuation. Ordinary use never calls it. See
[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) for the whole story live.

Attach mode gives up nothing on the server round trip, either. `FormidableValidator` exposes the
same `Engine` property, typed as `IFormValidationEngine`, and forwards both `ApplyServerIssues`
overloads itself — the sequence of issues, and the deserialized `FormidableValidationProblem` an
HTTP 400 arrives in:

```csharp
var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
_validator!.ApplyServerIssues(problem!);
```

So the server round trip is the same one line here as under `FormidableForm`, with no reaching
through `Engine` to reach it, and the same contract applies either way: each apply replaces the
previous server verdict, every issue lands at the severity it carries, and applying any of them
sets `HasSubmitted`, since a server response is treated as a submit result (see
[Server integration](server-integration.md)). Capture the component with `@ref`, and call from the
renderer's synchronization context. The engine exists from the moment the component binds to its
cascaded `EditContext`, so a call that beats the first render throws a message saying exactly that
rather than surfacing as a null reference from inside the component.

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
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FormidableMessageBase{TValue}.For"/>).</typeparam>
public sealed class FormidableCollectionMessage<TValue> : FormidableMessageBase<TValue>
{
    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
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
and the aria ids — instead of rendering any markup of its own:

```csharp
public sealed class FormidableFieldContext
{
    private readonly IFormValidationEngine _engine;

    internal FormidableFieldContext(
        IFormValidationEngine engine,
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

        var inputAttributes = new Dictionary<string, object>(4)
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
    /// </summary>
    public string? AriaDescribedBy { get; }

    /// <summary>
    /// The one-splat seam for a foreign control: <c>id</c>, <c>class</c>, and — only when
    /// applicable — <c>aria-invalid</c> and <c>aria-describedby</c>, bundled exactly as
    /// <see cref="ElementId"/>, <see cref="CssClass"/>, <see cref="AriaInvalid"/>, and
    /// <see cref="AriaDescribedBy"/> already report them. Splat it onto the control with
    /// <c>@attributes="field.InputAttributes"</c>; <see cref="NotifyChanged"/> is still the
    /// consumer's own wiring, since only the consumer's markup knows which native event commits
    /// the control's value.
    /// </summary>
    public IReadOnlyDictionary<string, object> InputAttributes { get; }

    /// <summary>
    /// Notifies the EditContext that the field changed, which is what marks it touched and runs the
    /// engine's live validation pass — call from a custom input's change handler.
    /// </summary>
    public void NotifyChanged() => _engine.EditContext.NotifyFieldChanged(Field);

    /// <summary>Marks the field touched without notifying a value change — call from a custom input's blur/focus-out handler.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);
}
```

*Source: `src/Formidable.Blazor/FormidableFieldContext.cs`*

Everything a hand-rolled control needs is on that context: `ElementId` for the id to render,
`CssClass` for the same state class a Formidable input would compute, `AriaInvalid`/
`AriaDescribedBy` for the same aria pair, `InputAttributes` to splat all four in one go, and
`NotifyChanged()`/`MarkTouched()` to drive the engine the way a Formidable input's own change
handler does internally. The worked example — wrapping a plain `<select>`, including how to
label it correctly — is one of the seams below, in
[The foreign-control pattern](#the-foreign-control-pattern).

## `FormidableFieldAnchor<TValue>`

A registration-only marker for a field rendered by markup Formidable doesn't wrap and that isn't
using `FormidableField` either — a raw `<input>`, a native `<select>` bound manually, a
third-party component. It renders nothing:

```csharp
public sealed class FormidableFieldAnchor<TValue> : FormidableComponentBase
{
    /// <summary>Accessor for the field to register, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>
    /// False: an anchor renders nothing, so a validation state change gives it nothing to
    /// re-render — registering the field is its whole job.
    /// </summary>
    protected override bool ObservesEngineState => false;

    /// <inheritdoc />
    private protected override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.Register(ResolveField(), KeepRegistered);
}
```

*Source: `src/Formidable.Blazor/FormidableFieldAnchor.cs`*

The whole component is its registration: the shared base does the binding, and an anchor is the one
component that opts out of the engine subscription, since it has no markup of its own to re-render.

Without something registering a field, its issues are permanently unrevealed, and placing a
`FormidableFieldAnchor` next to the raw control is the whole fix. See the Vanilla interop section
below and [Disclosure](disclosure.md) for the fuller "FormidableFieldAnchor for raw and foreign
controls" treatment.

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
mechanism), so a click can only reach an element that is actually rendered right now. A field
whose row sits outside a `Virtualize` container's current render window keeps its summary entry
(the issue is genuinely still there) but has no DOM element yet for the button to focus.
`FocusFallback` is the escape hatch for exactly that gap:

```csharp
    /// <summary>
    /// Invoked when a clicked issue's element is not in the DOM (focus miss) — e.g. a virtualized
    /// row outside the render window. Return <c>true</c> after making the element renderable
    /// (scrolling its container, expanding a section) and the summary retries the focus exactly
    /// once; return <c>false</c> to leave the miss as-is. When unset, a miss is silently ignored,
    /// matching the component's pre-fallback behaviour.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

Give `FormidableSummary` a `FocusFallback` for controls it might miss. The callback receives the field
identifier on a focus miss: make the element renderable (for example, scroll the virtualized
container to the row's offset), return `true`, and the summary retries the focus once.
[Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) below walks the sample's own
fallback end to end — its fallback scrolls by approximate row height and lets the retried focus
centre the row exactly. A fixed post-scroll delay keeps the sample honest and simple; a
production consumer might poll for the element instead.

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
    /// of on every form. A form's own <c>Options</c> parameter still wins where it is passed.
    /// Existing registrations are respected.
    /// </summary>
    /// <remarks>
    /// The configured instance is a singleton the whole app shares, and the engine re-reads its
    /// properties on every pass — so mutating it at runtime changes behaviour in every live form,
    /// not just the one being looked at.
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
options](options.md#app-wide-defaults).

## `FormidableCultureBootstrap`

A WebAssembly app downloads its satellite resource assemblies for whatever culture is current when
`RunAsync()` is called, and never again. So an app that lets a visitor choose a language has to
apply the stored choice *before* that line, not from a page afterwards — which is a small piece of
startup plumbing every localized WASM app writes the same way. `FormidableCultureBootstrap` is that
plumbing, ready-made:

```csharp
var host = builder.Build();

var js = host.Services.GetRequiredService<IJSRuntime>();
await FormidableCultureBootstrap.ApplyStoredCultureAsync(js, fallback: CultureInfo.GetCultureInfo("en-AU"));

await host.RunAsync();
```

It reads a culture name from `localStorage` (the key defaults to `formidable.culture`) and, when
the value names a culture the runtime recognises, sets both `CultureInfo.DefaultThreadCurrentCulture`
and `DefaultThreadCurrentUICulture` to it. A missing, blank, or unrecognisable value applies the
`fallback` instead; pass no fallback and the app simply keeps the culture it booted with. A stale
or corrupted stored value therefore cannot stop the app from starting.

A second overload takes a `Func<Task<string?>>` rather than an `IJSRuntime`, for storage that isn't
`localStorage` — a cookie, a user profile fetched from the server, a test's canned value:

```csharp
await FormidableCultureBootstrap.ApplyStoredCultureAsync(
    () => Task.FromResult<string?>(preferences.Language));
```

Neither overload has anything to do with validation, and both are WebAssembly-specific: a Blazor
Server host sets culture from the request instead, and does not use this class. FluentValidation's
own message translations follow `CultureInfo.CurrentUICulture` from there with no further wiring —
see [Profiles](profiles.md) for the localization story and the display-name half of it.

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
<FormidableForm Model="_order" Options="_options" OnValidSubmit="HandleValid">
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
visibility gate. What's new is `ScrollToRowAsync`, the code-behind method wired to `FocusFallback`
above:

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
it before returning `true`. The summary then retries the focus, and its own `scrollIntoView`
centres the row exactly.

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
                       aria-describedby="@NicknameMessagesId" /></label>
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
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses, this));
```

*Source: `src/Formidable.Blazor/FormValidationEngine.cs`*

(See [CSS and accessibility](css-and-accessibility.md) for exactly which classes that
provider applies, and how they differ from what a Formidable input's own `CssClass` computes.)

`FormidableFieldAnchor` next to the native `InputText` is what keeps it participating in
progressive disclosure at all. A plain `InputBase` never registers itself with Formidable's
`FieldRegistry`, so without the anchor its `ValidationMessage` would never receive an inline
error no matter what the validator reports.

The three attributes on that same line finish the crossing. A Formidable input renders
`FormidableFieldId.For(field)` as its element id, points `aria-describedby` at the matching
`-messages` id, and emits `aria-invalid="true"` while the field has errors. A native input
renders none of them, so the page derives them from the same sources the kit uses: a small
`NicknameId` property in the code-behind, and the engine's `GetFieldState(field).HasErrors` for
`aria-invalid` (a `null` value renders no attribute at all). The id is the entirety of what
`FormidableSummary`'s click-to-focus looks up, so with it the native field takes the summary's
click exactly like a wrapped one (see [CSS and accessibility](css-and-accessibility.md)). One
addition per concern: `FormidableFieldAnchor` for disclosure, the id for focus, `aria-describedby`
and `aria-invalid` for the assistive-technology story. Attributes derived from engine state need
the page to re-render when that state changes, so the sample subscribes to `Engine.StateChanged` —
the same subscription every kit component makes for itself.

**Samples:** [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), and
[`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor) for the CSS merge
(consumer `class` kept, computed state classes remapped onto a UI library's own).
