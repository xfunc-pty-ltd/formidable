# Collections and row-stable error identity

FluentValidation reports failures on indexed collection items as positional paths — `Items[0].Sku`,
`Teams[1].Members[0].Alias` — but a form's list is rarely static: rows get added, removed, and
reordered while the user works. A validator that keyed errors by that positional path would
misattribute them the moment a row moves. Formidable doesn't: it resolves every path down to the
actual object instance at that position and keys the error to the instance, not the index, so add,
remove, and reorder never separate an error from the row it belongs to.

## How instance-keyed resolution works

The model introspector parses a property path into segments — property names and indexer tokens —
and walks the live object graph one segment at a time. For an indexed segment it looks up the
actual item in the list at that position; the terminal segment never navigates further, it simply
names the field on whatever object the walk has reached:

```csharp
public readonly record struct ResolvedField(object Owner, string PropertyName);
```

*Source: `src/Formidable/Introspection/ResolvedField.cs`*

`Owner` is the deepest non-null object the walk actually reached — for `Teams[0].Members[1].Alias`,
that's the real `Member` instance currently at `Teams[0].Members[1]`. The engine turns that into a
Blazor `FieldIdentifier` built from the instance itself, not from the path string:

```csharp
    public static FieldIdentifier ToFieldIdentifier(this ResolvedField field, object rootModel, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(rootModel);

        if (field.Owner.GetType().IsValueType)
        {
            return new FieldIdentifier(rootModel, originalPath);
        }

        return new FieldIdentifier(field.Owner, field.PropertyName);
    }
```

*Source: `src/Formidable.Blazor/ResolvedFieldExtensions.cs`*

(The value-type branch is a fallback for owners `FieldIdentifier` structurally cannot hold — a
struct intermediate — and isn't the path collection rows take, since collection items are
reference types.) A `FieldIdentifier` built this way compares equal to another built from the same
object instance and property name regardless of where that object currently sits in its list — so
the error the engine stores against `Member` "ace" stays attached to that `Member`, in whatever
position it ends up after a reorder.

## The three page idioms

Instance-keyed resolution on the engine side only pays off if the markup gives it a stable
instance to key against. Three idioms, used together, make that true:

- **`@key` on the row instance, not the index.** Keying each row's root element by the object
  itself tells Blazor's diffing to keep that element (and its focus, scroll position, and any
  in-progress edit) attached to the row as it moves, instead of reusing DOM nodes by position.
- **`For` lambdas that capture the row instance.** `() => member.Alias` closes over the actual
  `Member` reference from that iteration of the loop, so `FieldIdentifier.Create(For)` on the
  client resolves to the exact same identity the introspector resolves on the validator side —
  both sides arrive at the same object independently, without coordinating.
- **`CollectionMessage` for collection-level rules, at every level that has one.** A `List<T>`
  property has no validated input of its own to register it, so a whole-collection rule (`Add at
  least one team`) would be permanently unrevealed without something registering that path.
  `CollectionMessage` both renders the collection-level issues and registers the field, and it's
  used once per collection that carries its own rule — the roster's teams and each team's members
  both get one.

## Nested collections

The sample nests two collections — teams, and each team's members — and exercises all three
idioms at both levels:

```razor
<FormidableForm Model="_roster">
    <FormSummary />
    <CollectionMessage For="() => _roster.Teams" />

    @foreach (var team in _roster.Teams)
    {
        <fieldset @key="team">
            <legend>Team</legend>
            <p><label>Name <FormidableInputText For="() => team.Name" @bind-Value="team.Name" /></label>
                <FieldMessage For="() => team.Name" /></p>

            <CollectionMessage For="() => team.Members" />
            <ul>
                @foreach (var member in team.Members)
                {
                    <li @key="member">
                        <label>Alias <FormidableInputText For="() => member.Alias" @bind-Value="member.Alias" /></label>
                        <FieldMessage For="() => member.Alias" />
                        <button type="button" @onclick="() => team.Members.Remove(member)">Remove</button>
                        <button type="button" @onclick="() => MoveUp(team.Members, member)">Move up</button>
                    </li>
                }
            </ul>
            <button type="button" @onclick="() => team.Members.Add(new Member())">Add member</button>
            <button type="button" @onclick="() => _roster.Teams.Remove(team)">Remove team</button>
            <button type="button" @onclick="() => MoveUp(_roster.Teams, team)">Move team up</button>
        </fieldset>
    }

    <button type="button" @onclick="() => _roster.Teams.Add(new Team())">Add team</button>
    <button type="submit">Submit</button>
</FormidableForm>
```

*Source: `samples/Formidable.Sample/Pages/Collections.razor`*

The validator behind it mirrors the nesting with `RuleForEach(...).ChildRules(...)`, one level
for teams and a second, nested level for each team's members:

```csharp
        RuleFor(r => r.Teams).NotEmpty().WithMessage("Add at least one team");
        RuleForEach(r => r.Teams).ChildRules(team =>
        {
            team.RuleFor(t => t.Name).NotEmpty().WithMessage("Team name is required");
            team.RuleFor(t => t.Members).NotEmpty().WithMessage("Every team needs at least one member");
            team.RuleForEach(t => t.Members).ChildRules(member =>
                member.RuleFor(m => m.Alias).NotEmpty().WithMessage("Alias is required"));
        });
```

*Source: `samples/Formidable.Sample.Shared/Roster.cs`*

Submit with gaps — an empty team name, an empty alias — then reorder or delete rows: each error
stays glued to its row, because `@key="team"` / `@key="member"` keep the right DOM element with
the right row, and the `For` lambdas keep resolving to the same instances the engine validated
against, regardless of the index either one currently occupies.

**Sample:** [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor)
