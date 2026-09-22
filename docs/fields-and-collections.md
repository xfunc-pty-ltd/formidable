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

The same resolution walks plain nesting, which is why depth needs no special handling at all:
`() => _invoice.BillTo.Street` is one field owned by the `BillTo` object, exactly as a row's field
is owned by its row. Compose the validators with `SetValidator` (or `ChildRules`) and the paths
line up on their own — see
[Recipes](recipes.md#i-want-to-validate-a-nested-object) for the worked pair.

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
    <label for="@field.ElementId">Payment terms</label>
    <select @attributes="field.InputAttributes"
            value="@_invoice.PaymentTerms" @onchange="args => OnTermsChanged(args, field)">
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

`field.InputAttributes` is the wiring in one splat: the element id, the state class, and the
`aria-invalid`/`aria-describedby` pair whenever they apply. `field.NotifyChanged()` stays yours to
call, because only your markup knows which event commits the control's value — and it does what a
Formidable input's own change handler does automatically: mark the field touched, tell the
`EditContext` it changed. Call `MarkTouched()` instead and the field goes touched without a live
pass ever running, which looks like validation silently doing nothing. The full pattern, including
why the label targets `field.ElementId` rather than wrapping the control and how a native input
reads the same context for its aria attributes, lives in
[Component kit](component-kit.md#the-foreign-control-pattern).

## A curated set of typed inputs, on purpose

Formidable ships five typed input components: `FormidableInputText`, `FormidableInputSelect`,
`FormidableInputTextArea`, `FormidableInputNumber`, and `FormidableInputDate`. That's not an
open-ended roadmap; it's the shape a headless kit is supposed to have. The seam is the contract:
`FormidableField` is how any control talks to the engine, and each typed input is one pre-wired
instance of that seam, shipped because wrapping it meaningfully improves the control's validation
UX — a text box, a dropdown, a text area, a number, and a date all clear that bar the same way.
Wrapping each once, in the kit, saves every consumer from rewriting the same handful of things for
their own: registration, ids, aria, the CSS merge, the change handler. A checkbox, a radio group, a
third-party date-picker widget, or any UI library's own input varies too much between projects for
one wrapper to serve them all, so those ride the `FormidableField` seam above, or a native input
paired with `FormidableFieldAnchor`.

Splatting `type="date"` (or `type="number"`) onto `FormidableInputText` works, as far as it goes:
the component binds a plain `string`, so the DOM's raw text lands in the model verbatim, with no
conversion and therefore no culture involved at all — the same way Workout's own date fields do
it, parsing the string by hand (`TryParseDate`) once it's in the model. What that pattern doesn't
give you is a typed model: the field stays a `string`, and turning it into a `DateOnly` or a
`decimal` is the page's job, every time.

The moment the model itself is the typed value — `DateOnly`, `decimal`, and so on, not a
string holding one — something has to convert the DOM text into it, and that's where a number or
date input earns its own wrapper. A native `<input type="number">`/`<input type="date">`'s DOM
value is a fixed, culture-invariant string — period-decimal for a number, ISO `yyyy-MM-dd` for a
date — but the base's own typed value binding resolves the *current thread's* culture, and the
two can disagree: under a comma-decimal culture it can misread `"12.5"` as `125`, and under a
non-Gregorian-calendar culture it can misread a date's year outright. `FormidableInputNumber` and
`FormidableInputDate` exist so that conversion is invariant by construction — matching the
browser's own culture-blind wire format — rather than something every consumer has to hand-roll
the way Workout's `TryParseDate` does. `FormidableInputDate` pairs with
`UpdateOn="InputUpdateMode.OnBlur"` by preference: Chromium fires a native date input's `change`
event once per typed segment (day, month, year), so the default `OnChange` can run a live pass
against a year the visitor hasn't finished typing — `OnBlur` commits the model on every segment
but validates only once, when the visitor moves on.

Reach for the typed input when the model is a number or a date; reach for the string-modelled
pattern — `FormidableInputText type="date"`/`type="number"`, with the page parsing by hand — when
the model deliberately stays a string instead (a raw, unparsed value the validator judges as
typed, the way an out-of-range entry and a non-numeric one might share one message, or where
hand-parsing needs to reject something a format string alone wouldn't, as Workout's year-range
check does).

Model an optional number or date as the nullable form — `int?`, `decimal?`, `DateOnly?`, and so
on — and an emptied box commits `null`, the same way a cleared `<select>` can: FluentValidation's
own rules speak from there (`NotNull()` for "required," `InclusiveBetween()` for a range). A
non-nullable `TValue` has no `null` to fall back to, so an emptied box — like any input the box
can't parse — reverts instead: the model stays whatever it already was, and when focus leaves the
field the control writes that value back into the box, so even text the browser displays while
reporting empty (a stray `e3` in a number box) cannot linger. Neither typed input raises a
message of its own for that; the message a visitor sees is always FluentValidation's, never a
binder's.

Five typed inputs, and a seam for everything else, is the whole shape. Not a promise of more
typed inputs to come.

**Next:** [Async and server](async-and-server.md)
