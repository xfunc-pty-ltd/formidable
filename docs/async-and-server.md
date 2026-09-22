# Async and server

A rule that has to ask something takes real time to answer, and a visitor typing at normal
speed doesn't wait for it. Fire the check on every keystroke and the answer for what they typed
a second ago can land after the answer for what they're typing now, showing a verdict for a
value that's already gone stale. Skip the check until submit and the same problem reappears on
the server: the identical rule has to run again there, because a form's client-side code is
never something a server can trust on its own. Formidable treats both as one story, not two —
an async rule gets the same profile and pending-state treatment any other rule gets, and the
server runs the identical validator, so its answer can join the client's without either side
inventing a second vocabulary for the same failure.

## What an async rule feels like

Nothing about writing one changes. FluentValidation's `MustAsync` and its siblings work exactly
as they do without Formidable, awaited like any other rule. What changes is what happens around
it: while the rule is in flight, the field it's checking carries a pending state
(`IsValidating`), so a page can show "checking…" instead of leaving the input looking idle.
`FormidableField`'s context exposes it as `field.State.IsValidating`, and the same flag drives a
`Pending` CSS class that Formidable's own inputs apply automatically.

```razor
<FormidableField For="() => _signup.Email" Context="field">
    <FormidableInputText @bind-Value="_signup.Email" />
    @if (field.State.IsValidating)
    {
        <span role="status">checking…</span>
    }
</FormidableField>
```

That's the whole visible surface of it. What decides *when* a check starts, what happens to one
still running when the value it was checking is already stale, and how a burst of keystrokes
resolves to one answer rather than several racing ones, is its own depth — covered in full
below.

## The server runs the same validator

Client-side rules only ever see what the browser already has. Anything that needs a source of
truth the browser doesn't have — is this coupon still valid, is this handle already taken by
someone else's account — has to be answered by the server. I didn't want the server to own a
second copy of those rules, or a subtly different one, so it doesn't: the same FluentValidation
validator the client runs answers the request server-side too, and a form and its endpoint can
never quietly disagree about what "required" means.

When the server rejects a submission, `FormidableForm.ApplyServerIssues` takes its answer and
applies it to the same fields the client's own errors would occupy. Each call replaces the
previous server verdict rather than piling onto it, so resubmitting never leaves a stale
duplicate message behind. `_form` below is the `FormidableForm` reference, captured on its
element with `@ref="_form"`.

```csharp
var response = await Http.PostAsJsonAsync("/api/signups", _signup);
if (!response.IsSuccessStatusCode)
{
    var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
    _form!.ApplyServerIssues(problem!);
}
```

That's the shape, not the whole story either. The debounce and cancellation rules that keep a
fast typist's async checks from racing each other live in
[Async validation](async-validation.md); the wire format a rejected request carries,
and how a minimal API or an MVC controller produces it, lives in
[Server integration](server-integration.md).

**Next:** [Recipes](recipes.md)
