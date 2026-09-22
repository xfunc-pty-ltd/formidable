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
it resolves what it needs from either an argument or the DI container, and refuses to guess:

```csharp
                Validator
                    ?? (IModelValidator<TModel>?)Services.GetService(typeof(IModelValidator<TModel>))
                    ?? throw new InvalidOperationException(
                        $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call services.AddFormidable() and register the FluentValidation validator."),
                (IModelIntrospector?)Services.GetService(typeof(IModelIntrospector))
                    ?? throw new InvalidOperationException("No IModelIntrospector is registered — call services.AddFormidable()."),
                Options ?? new FormidableOptions(),
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

An explicit `Validator` parameter wins if one is passed. Otherwise the form resolves
`IModelValidator<TModel>` from `Services.GetService`, and if DI has nothing either it throws a
message naming the missing type and the fix: `services.AddFormidable()` plus a registered
validator. The model introspector follows the same two-step fallback, with its own throw.
Neither fallback is configurable — it's the one place the kit fails loudly instead of quietly
doing nothing, and it's worth knowing about before an exception is the first time you meet it.

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
            inner.AddAttribute(4, "id", FormidableFieldId.For(new FieldIdentifier(Model, string.Empty)));
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
from `new FieldIdentifier(Model, string.Empty)`. That is the landing spot the all-suppressed
defensive gate's summary entry needs (see [Progressive disclosure](disclosure.md) and
[CSS and accessibility](css-and-accessibility.md)); a page using `FormidableForm` never has to
render it by hand. A consumer-splatted `id` or `tabindex` on `FormidableForm` is ignored for the
same reason a consumer-splatted `id` on `FormidableInputText` is: the value has to stay
deterministic for the focus service to find it. `FormidableValidator<TModel>` renders no `<form>`
of its own — see its section below for the attach-mode equivalent.

Swapping the `Model` parameter to a different instance — a draft load, a "start over" reset — is
the one thing that rebuilds the `EditContext` and engine; a component consuming `FormidableForm`
never manages that lifecycle itself:

```csharp
    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Model);

        if (!ReferenceEquals(_boundModel, Model))
        {
            _engine?.Dispose();
            _boundModel = Model;
            _editContext = new EditContext(Model);
            _engine = new FormValidationEngine<TModel>(
                Model,
                _editContext,
                Validator
                    ?? (IModelValidator<TModel>?)Services.GetService(typeof(IModelValidator<TModel>))
                    ?? throw new InvalidOperationException(
                        $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call services.AddFormidable() and register the FluentValidation validator."),
                (IModelIntrospector?)Services.GetService(typeof(IModelIntrospector))
                    ?? throw new InvalidOperationException("No IModelIntrospector is registered — call services.AddFormidable()."),
                Options ?? new FormidableOptions(),
                renderDispatch: work => InvokeAsync(work));
            _context = new FormidableFormContext(_engine);
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

`SubmitAsync()` is both the handler wired to the rendered `EditForm`'s `OnSubmit` and a public
method a consumer can call directly — a toolbar "Submit" button outside the form element, a
keyboard shortcut. It runs the submit pipeline and routes to `OnValidSubmit` or
`OnInvalidSubmit`:

```csharp
    /// <summary>
    /// Runs the submit pipeline programmatically. Call from the renderer's synchronization
    /// context (a Blazor event handler or <c>InvokeAsync</c>) — it triggers renders.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var outcome = await _engine!.ValidateForSubmitAsync();
        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync();
        }
        else
        {
            await OnInvalidSubmit.InvokeAsync(outcome);
        }

        StateHasChanged();
        return outcome;
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

The engine itself is exposed as a public property: `Engine => _engine`, typed as the non-generic
`IFormValidationEngine`. That property is exactly what the server round-trip reaches for
(`_form!.Engine!.ApplyServerIssues(issues)`; see [Server integration](server-integration.md)),
and what a page reads `IsValidating` or `HasSubmitted` from without going through a field.

## `FormidableInputText` and `FormidableInputBase<TValue>`

`FormidableInputBase<TValue>` is the base every validated input component builds on — the kit's
own and yours. It is public for that second reason: when the kit doesn't ship the control you
want, deriving from it is the supported way to get one (see [Deriving your own
input](#deriving-your-own-input) below). It provides five things so a concrete input only has to
render markup and call `AddValueBinding` once, right before closing its element, to wire whichever
value-commit attribute(s) `UpdateOn` calls for:

**Registration.** `OnParametersSet` resolves `For` to a `FieldIdentifier` and registers it with
the cascaded context's `FieldRegistry` — this is what makes the field's issues visible to
progressive disclosure while the component stays mounted (see
[Disclosure](disclosure.md)):

```csharp
    /// <summary>
    /// Resolves <see cref="For"/>, registers the field, and binds the engine subscription to the
    /// currently-cascaded context. A derived control that overrides this must call
    /// <c>base.OnParametersSet()</c>, or it registers nothing and never re-renders on a
    /// validation state change.
    /// </summary>
    protected override void OnParametersSet() =>
        _binding.Update(
            Context,
            GetType(),
            register: context =>
            {
                Field = FieldIdentifier.Create(For);
                ElementId = FormidableFieldId.For(Field);
                return context.Registry.Register(Field, KeepRegistered);
            },
            stateChanged: OnEngineStateChanged);
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

**Ids.** The same registration computes `ElementId` — the deterministic id every input, message
list, and the focus service address the field by (see
[CSS and accessibility](css-and-accessibility.md)).

**CSS.** `CssClass` merges any consumer-splatted `class` with the computed state class:

```csharp
    protected string CssClass =>
        CombineClassNames(AdditionalAttributes, FormidableCss.Compute(State, Context!.Engine.Options.CssClasses));
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
gets `formidable-invalid`/`formidable-valid`/`formidable-pending` appended — the two are merged,
never one replacing the other.

**Aria.** `AriaAttributes` supplies `aria-invalid` when the field has error-severity issues and
`aria-describedby` (pointing at the message list's id) when it has any issues at all, an empty
dictionary when the field is clean:

```csharp
    protected IReadOnlyDictionary<string, object> AriaAttributes
    {
        get
        {
            var state = State;
            var issues = Context!.Engine.GetIssues(Field);

            if (!state.HasErrors && issues.Count == 0)
            {
                return NoAriaAttributes;
            }

            var aria = new Dictionary<string, object>();
            if (state.HasErrors)
            {
                aria["aria-invalid"] = "true";
            }

            if (issues.Count > 0)
            {
                aria["aria-describedby"] = $"{ElementId}-messages";
            }

            return aria;
        }
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

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
            builder.AddAttribute(sequence + 1, "onblur", EventCallback.Factory.Create(this, NotifyChanged));
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TValue?>(this, v => SetCurrentValueAsync(v), Value));
        builder.SetUpdatesAttributeName("value");
    }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

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
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "id", ElementId);
        builder.AddAttribute(3, "class", CssClass);
        builder.AddMultipleAttributes(4, AriaAttributes!);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
```

*Source: `src/Formidable.Blazor/FormidableInputText.cs`*

The attribute order is the point: `AdditionalAttributes` splats first, and every value the
component computes — `id`, `class`, the aria attributes, and `AddValueBinding`'s own event
handlers — is added after, so each wins the duplicate-attribute race (Blazor applies
last-write-wins). That's also why a consumer-supplied `id` is silently ignored rather than
merged: the rendered id must always be the deterministic `FormidableFieldId`, because the message
list's `aria-describedby` target and `IFormidableFocusService` both address the field by it. The
same race catches a splatted `@onblur`: under `UpdateOn="OnBlur"`, `AddValueBinding`'s own
`onblur` wins the same way, so a consumer's own `@onblur` handler is silently lost rather than
merged. If markup needs to label the input without relying on implicit wrapping, address it by
the context's id instead of assuming a consumer id sticks:

```razor
    <div class="field"><label>Name <FormidableInputText For="() => _contact.Name" @bind-Value="_contact.Name" /></label>
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
below). A native `<input>` whose type only changes what the browser renders, not how a value
binds — a date or number input, say — clears that bar too, just without a dedicated wrapper:
`FormidableInputText type="date"` (or `"number"`) splats the type straight through
`AdditionalAttributes` and gets the same five extras any other `FormidableInputText` gets, with
`UpdateOn="InputUpdateMode.OnBlur"` answering the per-segment `change` events those types fire
natively (see [Options](options.md#updateon-per-input-not-a-formidableoptions-property)). Where a
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
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "type", "range");
        builder.AddAttribute(3, "id", ElementId);
        builder.AddAttribute(4, "class", CssClass);
        builder.AddMultipleAttributes(5, AriaAttributes!);
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
like any kit input — `<RatingInput For="() => _feedback.Rating" @bind-Value="_feedback.Rating"
min="0" max="5" />` — where `min` and `max` splat through `AdditionalAttributes` untouched.

Three rules apply to anything derived from the base:

- **Splat first, compute after.** `AdditionalAttributes` enters the render tree before `id`,
  `class` and the aria attributes, so the computed values win the duplicate-attribute race. That
  ordering is what merges the consumer's `class` and drops their `id`.
- **Call the base when you override `OnParametersSet`.** That is where the field resolves, registers
  and binds its engine subscription, and an override that skips `base` skips all three. Disposal is
  not yours to remember: `Dispose` is non-virtual and always releases the registration and the
  subscription, so a control with resources of its own overrides `DisposeCore` instead. The one
  exception is `IAsyncDisposable`, since Blazor calls only the async overload when a component
  implements both interfaces — a control with a `DisposeAsync` has to call `Dispose()` from it.
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
        builder.OpenElement(0, "select");
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "id", ElementId);
        builder.AddAttribute(3, "class", CssClass);
        builder.AddMultipleAttributes(4, AriaAttributes!);
        builder.AddAttribute(5, "value", FormatValueAsString(Value));
        builder.AddAttribute(
            6,
            "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, ApplyStringAsync, FormatValueAsString(Value)));
        builder.SetUpdatesAttributeName("value");
        builder.AddContent(7, ChildContent);
        builder.CloseElement();
    }
```

*Source: `src/Formidable.Blazor/FormidableInputSelect.cs`*

A `<select>` commits on its `change` event only — there is no meaningful `input` event distinct
from it, the way there is for a text box, and no per-segment `change` the way there is for a date
input — so this component always binds `onchange` regardless of which of `UpdateOn`'s three modes
is set. The parameter is still inherited (every `FormidableInputBase<TValue>` descendant has it),
so generic code that sets it on every kit input doesn't break; it simply has no effect here.

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
`Category`, required under the `Submit` ruleset exactly like `Slug`, is the select.

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
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "id", ElementId);
        builder.AddAttribute(3, "class", CssClass);
        builder.AddMultipleAttributes(4, AriaAttributes!);
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

## `FormidableFieldMessage<TValue>`

Every field needs somewhere to show what's wrong with it. `FormidableFieldMessage` renders a
field's current issues, any severity, as an accessible list, and nothing at all when the field
has none. It shares one base (`FormidableMessageBase<TValue>`) with its collection-level sibling
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
    private protected virtual FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) => null;
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

`FormidableFieldMessage` takes the base's default — it never registers:

```csharp
/// <summary>
/// Renders a field's current validation issues (any severity) as an accessible message list;
/// renders nothing when the field has none. Does not register with the field registry — messages
/// are not inputs, so pairing a message with a validated input (or a
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

Renders a live, severity-grouped list of every currently-visible issue across the form as a
`role="alert"` region — nothing while the form has no visible issues:

```csharp
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", "alert");
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

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

## `FormidableValidator<TModel>`, attaching to an existing form

A form that must attach to an `EditForm` it doesn't own — an existing page already built around a
plain `EditForm`/`EditContext` — doesn't need to give that up to use Formidable. It has an
alternative root, `FormidableValidator<TModel>`, for exactly that case; the full pattern,
including when to reach for it over `FormidableForm`, is covered in
[Migration guide](migration-guide.md).

Attach mode's one gap against `FormidableForm`: `FormidableValidator` renders no `<form>` element
of its own — it attaches to whatever `EditForm` the page already owns — so it has nowhere to put
the model-level gate id automatically. A page in attach mode still wants the all-suppressed
defensive gate's summary entry to land somewhere, so it renders that id itself, on the `EditForm`
it already has:

```razor
<EditForm Model="_model" OnValidSubmit="HandleValid" id="@GateId" tabindex="-1">
    <FormidableValidator TModel="Order" />
    ...
</EditForm>
```

```csharp
private string GateId => FormidableFieldId.For(new FieldIdentifier(_model, string.Empty));
```

This is the same pattern every `FormidableForm`-rooted page rendered by hand before the form took
it over; attach mode is the one place it still applies.

## `FormidableCollectionMessage<TValue>`

A `List<T>` property can carry its own rule — `RuleFor(x => x.Items).NotEmpty()` — with no single
control anywhere in the form to register the path that rule reports against.
`FormidableCollectionMessage` is `FormidableFieldMessage`'s collection-level sibling for exactly
that gap: identical rendering, but its override of the registration hook shown above makes it its
own registration:

```csharp
/// <summary>
/// Renders a collection-level field's current validation issues (any severity) as an accessible
/// message list — identical rendering to <see cref="FormidableFieldMessage{TValue}"/> — and
/// additionally registers the field with the field registry, so a collection-level rule's issues
/// are treated as revealed even though the collection itself (e.g. a <c>List&lt;T&gt;</c>
/// property) has no validated input of its own to register it. Renders nothing when the field
/// has no issues.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FormidableMessageBase{TValue}.For"/>).</typeparam>
public sealed class FormidableCollectionMessage<TValue> : FormidableMessageBase<TValue>
{
    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    private protected override FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) =>
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
        AriaDescribedBy = issues.Count > 0 ? $"{elementId}-messages" : null;
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
    /// the input's <c>aria-describedby</c>. Equal to <c>"{ElementId}-messages"</c>.
    /// </summary>
    public string? AriaDescribedBy { get; }

    /// <summary>Marks the field touched and notifies the EditContext that it changed — call from a custom input's change handler.</summary>
    public void NotifyChanged()
    {
        MarkTouched();
        _engine.EditContext.NotifyFieldChanged(Field);
    }

    /// <summary>Marks the field touched without notifying a value change — call from a custom input's blur/focus-out handler.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);
}
```

*Source: `src/Formidable.Blazor/FormidableFieldContext.cs`*

Everything a hand-rolled control needs is on that context: `ElementId` for the id to render,
`CssClass` for the same state class a Formidable input would compute, `AriaInvalid`/
`AriaDescribedBy` for the same aria pair, and `NotifyChanged()`/`MarkTouched()` to drive the
engine the way a Formidable input's own change handler does internally. The worked example —
wrapping a plain `<select>`, including how to label it correctly — is one of the seams below, in
[The foreign-control pattern](#the-foreign-control-pattern).

## `FormidableFieldAnchor<TValue>`

A registration-only marker for a field rendered by markup Formidable doesn't wrap and that isn't
using `FormidableField` either — a raw `<input>`, a native `<select>` bound manually, a
third-party component. It renders nothing:

```csharp
public sealed class FormidableFieldAnchor<TValue> : ComponentBase, IDisposable
{
    private readonly FormContextBinding _binding = new();

    [CascadingParameter]
    private FormidableFormContext? Context { get; set; }

    /// <summary>Accessor for the field to register, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet() =>
        _binding.Update(
            Context,
            GetType(),
            register: context => context.Registry.Register(FieldIdentifier.Create(For), KeepRegistered));

    /// <inheritdoc />
    public void Dispose() => _binding.Dispose();
}
```

*Source: `src/Formidable.Blazor/FormidableFieldAnchor.cs`*

Without something registering a field, its issues are permanently unrevealed, and placing a
`FormidableFieldAnchor` next to the raw control is the whole fix. See the Vanilla interop section
below and [Disclosure](disclosure.md) for the fuller "FormidableFieldAnchor for raw and foreign
controls" treatment.

## The foreign-control pattern

`FormidableField`'s sample page wraps a plain `<select>` — a control Formidable does not, and
cannot know how to, wrap itself:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid">
    <FormidableSummary />

    <FormidableField For="() => _order.Colour" Context="field">
        <div class="field">
            <label for="@field.ElementId">Colour</label>
            <select id="@field.ElementId" class="@field.CssClass"
                    aria-invalid="@(field.AriaInvalid ? "true" : null)"
                    aria-describedby="@field.AriaDescribedBy"
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
mirrors) plus the focus service:

```csharp
namespace Formidable.Blazor;

/// <summary>Dependency-injection registration for Formidable's Blazor integration.</summary>
public static class FormidableBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Registers Formidable's core services (see <see cref="FormidableServiceCollectionExtensions.AddFormidable"/>)
    /// plus <see cref="IFormidableFocusService"/>. The one-call registration for Blazor consumers.
    /// Existing registrations are respected.
    /// </summary>
    public static IServiceCollection AddFormidableBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFormidable();
        services.TryAddScoped<IFormidableFocusService, FormidableFocusService>();
        return services;
    }
}
```

*Source: `src/Formidable.Blazor/FormidableBlazorServiceCollectionExtensions.cs`*

`TryAddScoped` means a consumer that has already registered its own `IFormidableFocusService` (a
custom focus/scroll behavior) keeps it — `AddFormidableBlazor()` never overwrites an existing
registration.

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
    <FormidableSummary FocusFallback="ScrollToRowAsync" />

    <div class="scroll-panel">
        <Virtualize Items="_order.Gadgets" ItemSize="RowHeight" Context="gadget">
            <div class="field" @key="gadget">
                <label>Serial
                    <FormidableInputText For="() => gadget.Serial" @bind-Value="gadget.Serial" KeepRegistered="true" />
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
    private const float RowHeight = 96f;
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
    <FormidableSummary />

    <div class="field">
        <label>Nickname (native InputText)
            <InputText @bind-Value="_order.Nickname"
                       id="@NicknameId"
                       aria-invalid="@NicknameAriaInvalid"
                       aria-describedby="@($"{NicknameId}-messages")" /></label>
        <ValidationMessage For="() => _order.Nickname" id="@($"{NicknameId}-messages")" />
        <FormidableFieldAnchor For="() => _order.Nickname" />
    </div>

    <div class="field">
        <label>Colour (Formidable input)
            <FormidableInputText For="() => _order.Colour" @bind-Value="_order.Colour" /></label>
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
