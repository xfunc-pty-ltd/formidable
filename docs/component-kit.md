# The component kit

Formidable's Blazor package ships a component kit with no visual opinion at all — every piece
renders unstyled markup (or, for the renderless pieces, no markup of its own), expressing
validation state purely as class names and ARIA attributes for a consumer's own CSS to style (see
[`docs/css-and-accessibility.md`](css-and-accessibility.md)). Nothing in this kit assumes a
particular UI library: the two ready-made shapes (`FormidableForm`, `FormidableInputText`) are a
convenient default, and the renderless shapes (`FormidableField`, `FieldAnchor`,
`FieldMessage`/`CollectionMessage`) exist specifically so any UI library — or plain HTML — can
sit on top of the same engine without being wrapped by it.

## `FormidableForm<TModel>`

The primary root component. It owns the `EditContext` — nothing else in a Formidable-based form
creates or replaces one — and renders a real `EditForm` underneath, so anything that already
expects standard Blazor forms interop (native `InputBase` descendants, `ValidationMessage`, a
`DataAnnotationsValidator` alongside it) keeps working:

```csharp
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "Value", _context);
        builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenComponent<EditForm>(0);
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            if (AdditionalAttributes is not null)
            {
                inner.AddMultipleAttributes(3, AdditionalAttributes!);
            }
            inner.AddComponentParameter(4, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent ?? (_ => { })));
            inner.CloseComponent();
        }));
        builder.CloseComponent();
        // Not IsFixed: the context instance is replaced whenever Model is swapped (a new
        // engine/EditContext pair), and descendants must observe the replacement.
    }
```

*Source: `src/Formidable.Blazor/FormidableForm.cs`*

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
method a consumer can call directly (a toolbar "Submit" button outside the form element, a
keyboard shortcut) — it runs the submit pipeline and routes to `OnValidSubmit` or
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

The engine itself is exposed as a public property — `Engine => _engine`, typed as the non-generic
`IFormValidationEngine` — which is exactly what the server round-trip reaches for
(`_form!.Engine!.ApplyServerIssues(issues)`; see
[`docs/server-integration.md`](server-integration.md)) and what a page reads `IsValidating` or
`HasSubmitted` from without going through a field.

A form that must attach to an `EditForm` it doesn't own — an existing page already built around a
plain `EditForm`/`EditContext` — has an alternative root, `FormidableValidator<TModel>`, covered
in [`docs/migration-guide.md`](migration-guide.md).

## `FormidableInputText` and `ValidatedInputBase<TValue>`

`ValidatedInputBase<TValue>` is the base every validated input component builds on. It provides
five things so a concrete input only has to render markup and call one method from its change
handler:

**Registration.** `OnParametersSet` resolves `For` to a `FieldIdentifier` and registers it with
the cascaded context's `FieldRegistry` — this is what makes the field's issues visible to
progressive disclosure while the component stays mounted (see
[`docs/disclosure.md`](disclosure.md)):

```csharp
    /// <inheritdoc />
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

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

**Ids.** The same registration computes `ElementId` — the deterministic id every input, message
list, and the focus service address the field by (see
[`docs/css-and-accessibility.md`](css-and-accessibility.md)).

**CSS.** `CssClass` merges any consumer-splatted `class` with the computed state class:

```csharp
    protected string CssClass =>
        CombineClassNames(AdditionalAttributes, FormidableCss.Compute(State, Context!.Engine.Options.CssClasses));
```

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

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

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

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

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

**`SetCurrentValueAsync`.** The one call a concrete input's change handler makes — it assigns
`Value`, invokes `ValueChanged`, marks the field touched, and notifies the `EditContext` so the
engine's live validation pass runs:

```csharp
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        Value = value;
        if (ValueChanged.HasDelegate)
        {
            await ValueChanged.InvokeAsync(value);
        }

        Context!.Engine.MarkTouched(Field);
        Context.EditContext.NotifyFieldChanged(Field);
    }
```

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

`FormidableInputText` is the reference implementation — a plain `<input>` wired through all five,
and a working example of how little markup the base class leaves to write:

```csharp
public sealed class FormidableInputText : ValidatedInputBase<string?>
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
        builder.AddAttribute(
            6,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, v => SetCurrentValueAsync(v), Value));
        builder.CloseElement();
    }
}
```

*Source: `src/Formidable.Blazor/FormidableInputText.cs`*

The attribute order is the point: `AdditionalAttributes` splats first, and every value the
component computes — `id`, `class`, the aria attributes — is added after, so each wins the
duplicate-attribute race (Blazor applies last-write-wins). That's also why a consumer-supplied
`id` is silently ignored rather than merged: the rendered id must always be the deterministic
`FormidableFieldId`, because the message list's `aria-describedby` target and
`IFormidableFocusService` both address the field by it. If markup needs to label the input without
relying on implicit wrapping, address it by the context's id instead of assuming a consumer id
sticks:

```razor
    <div class="field"><label>Name <FormidableInputText For="() => _contact.Name" @bind-Value="_contact.Name" /></label>
        <FieldMessage For="() => _contact.Name" /></div>
```

*Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor`*

— label-wrapping is what every sample using `FormidableInputText` does, since the label needs no
explicit `for` when it wraps the control. `FormidableField`'s renderless template is the other
option, for markup that isn't wrapping (see `ForeignControl.razor` below, which uses
`<label for="@field.ElementId">` because the control it labels isn't a Formidable component at
all).

## `FormidableField<TValue>` and `FormidableFieldContext`

`FormidableField` is the any-UI-library integration point: a renderless component that registers
its field and, on every render, hands its `ChildContent` a fresh `FormidableFieldContext` —
state, issues, computed CSS class, and the aria ids — instead of rendering any markup of its own:

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

The sample page for it wraps a plain `<select>` — a control Formidable does not, and cannot know
how to, wrap itself:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid">
    <FormSummary />

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
        <FieldMessage For="() => _order.Colour" />
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

`field.NotifyChanged()` in the change handler is doing exactly what `SetCurrentValueAsync` does
for `ValidatedInputBase` descendants — mark touched, notify the `EditContext` — just called
explicitly instead of being baked into a base class, because there is no base class here to bake
it into.

## `FieldAnchor<TValue>`

A registration-only marker for a field rendered by markup Formidable doesn't wrap and that isn't
using `FormidableField` either — a raw `<input>`, a native `<select>` bound manually, a
third-party component. It renders nothing:

```csharp
public sealed class FieldAnchor<TValue> : ComponentBase, IDisposable
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

*Source: `src/Formidable.Blazor/FieldAnchor.cs`*

Without something registering a field, its issues are permanently unrevealed — placing a
`FieldAnchor` next to the raw control is the whole fix; see the Vanilla interop section below and
[`docs/disclosure.md`](disclosure.md) for the fuller "FieldAnchor for raw and foreign controls"
treatment.

## `FieldMessage<TValue>` and `CollectionMessage<TValue>`

Both share one internal base (`FieldMessageBase<TValue>`) for resolving `For`, subscribing to the
engine's `StateChanged`, and rendering the same accessible message list. The one thing they
differ on is whether rendering the message list also registers the field:

```csharp
    /// <summary>
    /// Registers <paramref name="field"/> with <paramref name="context"/>'s field registry, or
    /// returns null to skip registration. Messages are not inputs, so the base implementation
    /// (used by <see cref="FieldMessage{TValue}"/>) never registers; <see cref="CollectionMessage{TValue}"/>
    /// overrides this to mark its collection-level path revealed so collection-level rules
    /// surface even though the collection itself has no validated input registering it.
    /// </summary>
    private protected virtual FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) => null;
```

*Source: `src/Formidable.Blazor/FieldMessage.cs`*

```csharp
/// <summary>
/// Renders a field's current validation issues (any severity) as an accessible message list;
/// renders nothing when the field has none. Does not register with the field registry — messages
/// are not inputs, so pairing a message with a validated input (or a <see cref="FieldAnchor{TValue}"/>)
/// elsewhere in the form is what keeps the field revealed.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FieldMessageBase{TValue}.For"/>).</typeparam>
public sealed class FieldMessage<TValue> : FieldMessageBase<TValue>
{
}
```

*Source: `src/Formidable.Blazor/FieldMessage.cs`*

```csharp
/// <summary>
/// Renders a collection-level field's current validation issues (any severity) as an accessible
/// message list — identical rendering to <see cref="FieldMessage{TValue}"/> — and additionally
/// registers the field with the field registry, so a collection-level rule's issues are treated
/// as revealed even though the collection itself (e.g. a <c>List&lt;T&gt;</c> property) has no
/// validated input of its own to register it. Renders nothing when the field has no issues.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FieldMessageBase{TValue}.For"/>).</typeparam>
public sealed class CollectionMessage<TValue> : FieldMessageBase<TValue>
{
    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    private protected override FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) =>
        context.Registry.Register(field, KeepRegistered);
}
```

*Source: `src/Formidable.Blazor/CollectionMessage.cs`*

Practically: `FieldMessage` always needs to be paired with something else that registers the same
field (a `ValidatedInputBase` descendant, `FormidableField`, or `FieldAnchor`) or its messages
stay permanently unrevealed. `CollectionMessage` needs no such pairing — it is its own
registration, because a `List<T>` property with a collection-level rule (`RuleFor(x => x.Items).NotEmpty()`)
otherwise has no rendered input to register the path at all. See
[`docs/collections-and-row-identity.md`](collections-and-row-identity.md) for the nested-collection
pattern this exists for.

## `FormSummary`

Renders a live, severity-grouped list of every currently-visible issue across the form as a
`role="alert"` region — nothing while the form has no visible issues:

```csharp
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", "alert");
```

*Source: `src/Formidable.Blazor/FormSummary.cs`*

Each item is a button that moves focus to the offending field through `IFormidableFocusService`,
via the component's own `FocusWithFallbackAsync` (the fallback hop described below):

```csharp
                builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, () => FocusWithFallbackAsync(visibleIssue.Field)));
```

*Source: `src/Formidable.Blazor/FormSummary.cs`*

**`FocusFallback`.** `FocusAsync` locates the target by DOM id (see
[`docs/css-and-accessibility.md`](css-and-accessibility.md) for the focus service's mechanism), so
click-to-focus can only reach an element that is actually rendered right now — a field whose row
sits outside a `Virtualize` container's current render window keeps its summary entry (the issue is
genuinely still there) but has no DOM element yet for the button to focus. `FocusFallback` is the
escape hatch for exactly that gap:

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

*Source: `src/Formidable.Blazor/FormSummary.cs`*

Click-to-focus targets elements in the DOM. For virtualized rows outside the render window, give
`FormSummary` a `FocusFallback`: it receives the field identifier on a focus miss; make the element
renderable (for example, scroll the virtualized container to the row's offset), return `true`, and
the summary retries the focus once. The sample's Virtualize page
([`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor)) shows the pattern — its
fallback scrolls by approximate row height and lets the retried focus centre the row exactly. A
fixed post-scroll delay keeps the sample honest and simple; a production consumer might poll for
the element instead.

## `AddFormidableBlazor()`

The one-call registration for a Blazor client — everything `AddFormidable()` registers (see
[`docs/server-integration.md`](server-integration.md) for the server-side registration this
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

## Virtualize + `KeepRegistered`

Every registering component exposes a `KeepRegistered` parameter (`ValidatedInputBase<TValue>`
descendants, `FieldAnchor`, `FormidableField`, `CollectionMessage`). A `Virtualize` container
disposes rows that scroll out of view even though they remain part of the form; without
`KeepRegistered`, a scrolled-away row's field would unregister and its already-showing error would
go quiet, even though the row still exists in the model. The sample page pairs that with
`FormSummary`'s `FocusFallback` (see above), so a row far outside the render window is both kept
disclosed and reachable by a summary click:

```razor
<FormidableForm Model="_order" Options="_options" OnValidSubmit="HandleValid">
    <FormSummary FocusFallback="ScrollToRowAsync" />

    <div class="scroll-panel">
        <Virtualize Items="_order.Gadgets" ItemSize="RowHeight" Context="gadget">
            <div class="field" @key="gadget">
                <label>Serial
                    <FormidableInputText For="() => gadget.Serial" @bind-Value="gadget.Serial" KeepRegistered="true" />
                </label>
                <FieldMessage For="() => gadget.Serial" />
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
`FormSummary` no matter how far it scrolls, exactly as before. The sample also sets a
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

Clicking a summary entry for a row inside the current render window still focuses it directly. For
a row scrolled far away, the miss triggers `ScrollToRowAsync`, which scrolls `.scroll-panel` to the
row's approximate offset (`index * RowHeight`) and waits 120ms for `Virtualize` to render it before
returning `true` — the summary then retries the focus, and its own `scrollIntoView` centres the row
exactly. See `FocusFallback` above for the general mechanism this page demonstrates.

## Vanilla interop

Because `FormidableForm` renders a real `EditForm`, plain Blazor form components work inside it
unmodified — a native `InputText` and `ValidationMessage` beside a Formidable input in the same
form:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid">
    <FormSummary />

    <div class="field"><label>Nickname (native InputText) <InputText @bind-Value="_order.Nickname" /></label>
        <ValidationMessage For="() => _order.Nickname" />
        <FieldAnchor For="() => _order.Nickname" /></div>

    <div class="field"><label>Colour (Formidable input) <FormidableInputText For="() => _order.Colour" @bind-Value="_order.Colour" /></label>
        <FieldMessage For="() => _order.Colour" /></div>

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
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses));
```

*Source: `src/Formidable.Blazor/FormValidationEngine.cs`*

(See [`docs/css-and-accessibility.md`](css-and-accessibility.md) for exactly which classes that
provider applies, and how they differ from what a Formidable input's own `CssClass` computes.)

`FieldAnchor` next to the native `InputText` is what keeps it participating in progressive
disclosure at all — a plain `InputBase` never registers itself with Formidable's `FieldRegistry`,
so without the anchor its `ValidationMessage` would never receive an inline error no matter what
the validator reports.

## Samples

**Samples:** [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor).
