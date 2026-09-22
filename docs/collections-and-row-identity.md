# Collections and row-stable error identity

**You should already know:** the draft/submit split ([Core concepts](core-concepts.md)),
and the row-stable habits — `@key` by instance, `For` closed over it — at a glance
([Fields and collections](fields-and-collections.md)).

Delete row two from a list of three and, in a validator that keys errors by position, row three
quietly inherits row two's error. FluentValidation reports collection failures as positional
paths: `Items[0].Sku`, `Teams[1].Members[0].Alias`. Positions are the one thing about a list that
never survives an edit. Add a row, remove one, or drag two into a different order, and every index
below the change now names a different object than the one the validator meant. Key an error
against that path string and the mistake above stops being hypothetical: an error meant for the
row that's gone shows up on the row that inherited its position, a field the user already fixed
looks broken again after a reorder, and the form ends up complaining about the wrong line.

Formidable never keys by path. It resolves every failure down to the actual object sitting at
that position and keys the error to the instance itself, so add, remove, and reorder can never
separate an error from the row it belongs to. This page covers the three markup idioms that make
that possible, how the engine's resolution works underneath them, and how both hold up once one
collection nests inside another.

## Need to know

Three idioms turn that resolution into row-stable markup: `@key` by instance, a `For` lambda
closing over the same instance, and a `CollectionMessage` for every collection-level rule.

```razor
                <ul class="member-list">
                    @foreach (var member in team.Members)
                    {
                        <li class="field" @key="member">
                            <label>Alias <FormidableInputText For="() => member.Alias" @bind-Value="member.Alias" /></label>
                            <FieldMessage For="() => member.Alias" />
                            <div class="actions">
                                <button type="button" @onclick="() => team.Members.Remove(member)">Remove</button>
                                <button type="button" @onclick="() => MoveUp(team.Members, member)">Move up</button>
                            </div>
                        </li>
                    }
                </ul>
```

*Excerpt from `samples/Formidable.Sample/Pages/Collections.razor`*

`@key="member"` keys the `<li>` by the object itself, not its position in the list. Blazor's
diffing then keeps that element attached to the row as it moves, instead of reusing DOM nodes by
index, so its focus, scroll position, and any in-progress edit travel with the row.
`() => member.Alias` closes over the actual `Member` reference from this iteration of the loop, so
`FieldIdentifier.Create(For)` on the client resolves to the exact identity the introspector
resolves independently on the validator side. Both sides arrive at the same object without
coordinating.

Neither idiom helps a rule that has no field of its own. `Teams` and `Members` are both `List<T>`
properties, so nothing renders an input for the list itself. A whole-collection rule like "Add at
least one team" would have nowhere to register and nowhere to become visible. `CollectionMessage`
closes that gap:

```razor
    <CollectionMessage For="() => _roster.Teams" />
```

*Excerpt from `samples/Formidable.Sample/Pages/Collections.razor`*

It renders the collection-level issues and registers the field in the same component, used once
per collection that carries its own rule — the roster's teams and each team's members both get
one.

That's the whole authoring surface: key the row, close the lambda, register the collection. What
the engine does with those three habits is the rest of this page.

## How instance-keyed resolution works

Reading a path string as an address would make the idioms above cosmetic. Key every `<li>` by
instance and Blazor still shows the right DOM element, but the error handed to it is already
wrong. A validator matching purely on `Teams[0].Members[1]` files that failure against whatever
object next occupies that slot, once a remove or reorder changes what's there. That mis-filing
happens before any markup gets involved.

Formidable doesn't read it as an address. The model introspector parses each property path into
segments — property names and indexer tokens — and walks the live object graph one segment at a
time. For an indexed segment it looks up the actual item sitting in the list at that position; the
terminal segment never navigates further, it just names the field on whatever object the walk has
reached:

```csharp
public readonly record struct ResolvedField(object Owner, string PropertyName);
```

*Source: `src/Formidable/Introspection/ResolvedField.cs`*

`Owner` is the deepest non-null object the walk actually reached — for `Teams[0].Members[1].Alias`,
that's the real `Member` instance currently sitting at that position. The engine turns the result
into a Blazor `FieldIdentifier` built from the instance itself, not the path string:

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

(The value-type branch is a fallback for owners `FieldIdentifier` structurally can't hold — a
struct intermediate — and isn't the path collection rows take, since collection items are
reference types.) A `FieldIdentifier` built this way compares equal to another built from the same
object instance and property name, no matter where that object currently sits in its list. The
error the engine stores against `Member` "ace" therefore stays attached to that `Member`, however
a reorder rearranges the list around it.

## Nested collections

The sample nests two collections — teams, and each team's members — and exercises all three
idioms at both levels:

```razor
<FormidableForm Model="_roster" OnValidSubmit="HandleValid">
    <FormSummary />
    <CollectionMessage For="() => _roster.Teams" />

    <div class="team-list" id="@TeamsId" tabindex="-1">
        @foreach (var team in _roster.Teams)
        {
            <fieldset @key="team" id="@MembersId(team)" tabindex="-1">
                <legend>Team</legend>
                <div class="field">
                    <label>Name <FormidableInputText For="() => team.Name" @bind-Value="team.Name" /></label>
                    <FieldMessage For="() => team.Name" />
                </div>

                <CollectionMessage For="() => team.Members" />
                <ul class="member-list">
                    @foreach (var member in team.Members)
                    {
                        <li class="field" @key="member">
                            <label>Alias <FormidableInputText For="() => member.Alias" @bind-Value="member.Alias" /></label>
                            <FieldMessage For="() => member.Alias" />
                            <div class="actions">
                                <button type="button" @onclick="() => team.Members.Remove(member)">Remove</button>
                                <button type="button" @onclick="() => MoveUp(team.Members, member)">Move up</button>
                            </div>
                        </li>
                    }
                </ul>
                <div class="actions">
                    <button type="button" @onclick="() => team.Members.Add(new Member())">Add member</button>
                    <button type="button" @onclick="() => _roster.Teams.Remove(team)">Remove team</button>
                    <button type="button" @onclick="() => MoveUp(_roster.Teams, team)">Move team up</button>
                </div>
            </fieldset>
        }
    </div>

    <div class="actions">
        <button type="button" @onclick="() => _roster.Teams.Add(new Team())">Add team</button>
        <button type="submit">Submit</button>
    </div>
</FormidableForm>
```

*Excerpt from `samples/Formidable.Sample/Pages/Collections.razor`* — the page also carries a
teaching panel above the form; the `class` attributes belong to the sample app's own styling,
since the library ships none. The `id`/`tabindex` pair on each container is what gives a
collection's summary entry somewhere to land. A collection rule fails against the list, not
against any one input, so the element carrying the collection's `FormidableFieldId` is what takes
the click (see [CSS and accessibility](css-and-accessibility.md)).

The validator behind it mirrors the nesting with `RuleForEach(...).ChildRules(...)`, one level for
teams and a second, nested level for each team's members:

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

Submit with gaps — an empty team name, an empty alias — then reorder or delete rows. Each error
stays put on its row, because `@key="team"` / `@key="member"` keep the right DOM element attached
to the right row. The `For` lambdas keep resolving to the same instances the engine validated
against, regardless of the index either one currently occupies.

**Sample:** [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor)
