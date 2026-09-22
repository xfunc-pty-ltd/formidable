# Fields and collections

A field's validation message is the easy part until the field sits inside a list. Delete row
two from an order and every error that belonged to row three has to travel with it: key those
errors by position in the list, and one deleted row misattributes every message below it to the
wrong line. Bring a UI library into the mix and the mismatch widens, since half the controls on
a real page aren't `<input>` elements Formidable ships a component for at all. Formidable
answers both the same way: a field is the object that owns the value, addressed directly — not
the index it happens to occupy, and not tied to which HTML element renders it.

## Row-stable identity: errors keyed to the object, not the index

Every property path FluentValidation reports is positional — `Lines[0].Sku`, `Lines[1].Sku` —
but Formidable never keys an error against that path string. It resolves the path down to the
actual object sitting at that position and keys the error to the instance itself, so add,
remove, and reorder can never misattribute a message to the wrong row. The full mechanism lives
in [Collections and row identity](collections-and-row-identity.md); what it buys the
markup is two habits. Key each row's root element by the row instance, not its index, so
Blazor's diffing keeps the right DOM element attached to the right row as the list changes. And
write each `For` lambda as a closure over that same instance:

```razor
<FormidableCollectionMessage For="() => _invoice.Lines" />
@foreach (var line in _invoice.Lines)
{
    <div class="field" @key="line">
        <label>SKU <FormidableInputText For="() => line.Sku" @bind-Value="line.Sku" /></label>
        <FormidableFieldMessage For="() => line.Sku" />
    </div>
}
```

The engine resolves `line.Sku` to the same object on the validator side, independently, with
nothing coordinating the two — they just both start from the object, never the position.

`FormidableFieldMessage` and `FormidableCollectionMessage` split one job.
`FormidableFieldMessage` renders a field's own issues but registers nothing itself — it needs a
nearby input (or a `FormidableField`, or a `FormidableFieldAnchor`) to register that path, or its
messages stay permanently unrevealed. `FormidableCollectionMessage` does its own registering,
because a `List<T>` property like `Lines` has no input of its own for a collection-level rule
(`Add at least one line`) to attach to — without it, that rule's failure would have nowhere in
the markup to become visible at all.

## `FormidableField`, a first look

Not every control on a form is one Formidable wraps for you. A UI library's own `<select>`, a
checkbox group, a third-party date-picker widget: none of those are `FormidableInputText`, and
none of them need to be. `FormidableField` is the renderless seam for exactly that case. It
registers the field and hands its content a `FormidableFieldContext` — the field's state, its
computed CSS class, its element id — instead of rendering any markup of its own. The `<select>`
below is only a stand-in for that case — the kit now ships `FormidableInputSelect` for a plain
select, so reach for this seam when the control is one the kit doesn't wrap, a UI library's own
select being the common example.

```razor
<FormidableField For="() => _invoice.PaymentTerms" Context="field">
    <select id="@field.ElementId" class="@field.CssClass"
            @onchange="args => OnTermsChanged(args, field)">
        <option value="">Choose…</option>
        <option>Net 30</option>
        <option>Net 60</option>
    </select>
</FormidableField>
```

```csharp
private void OnTermsChanged(ChangeEventArgs args, FormidableFieldContext field)
{
    _invoice.PaymentTerms = args.Value?.ToString() ?? string.Empty;
    field.NotifyChanged();
}
```

`field.NotifyChanged()` does what a Formidable input's own change handler does automatically —
mark the field touched, tell the `EditContext` it changed — just spelled out by hand, because
there's no base class here to hide it inside. The full pattern, including how to label a
foreign element correctly and how a native input reads the same context for its aria
attributes, lives in
[Component kit](component-kit.md#the-foreign-control-pattern).

## A curated set of typed inputs, on purpose

Formidable ships three typed input components: `FormidableInputText`, `FormidableInputSelect`,
and `FormidableInputTextArea`. That's not an open-ended roadmap; it's the shape a headless kit is
supposed to have. The seam is the contract: `FormidableField` is how any control talks to the
engine, and each typed input is one pre-wired instance of that seam, shipped because wrapping it
meaningfully improves the control's validation UX — a text box, a dropdown, and a text area all
clear that bar the same way. Wrapping each once, in the kit, saves every consumer from rewriting
the same handful of things for their own: registration, ids, aria, the CSS merge, the change
handler. A checkbox, a radio group, a third-party date-picker widget, or any UI library's own
input varies too much between projects for one wrapper to serve them all, so those ride the
`FormidableField` seam above, or a native input paired with `FormidableFieldAnchor` — a native
date input's home, by contrast, is `FormidableInputText type="date" UpdateOn="OnBlur"`. Three
typed inputs, and a seam for everything else, is the whole shape.
Not a promise of more typed inputs to come.

**Next:** [Async and server](async-and-server.md)
