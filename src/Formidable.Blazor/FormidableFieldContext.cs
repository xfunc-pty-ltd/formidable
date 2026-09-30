using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>What <see cref="FormidableField{TValue}"/> hands its child content each render: the field's state, issues, class and the attributes a custom input binds.</summary>
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

    /// <summary>The element id for the field's input, as <see cref="FormidableFieldId.For(FieldIdentifier)"/> derives it.</summary>
    public string ElementId { get; }

    /// <summary>The field's current <see cref="FieldState"/>: touched, modified, being checked, the severities it carries, and <see cref="FieldState.WouldPassSubmit"/>.</summary>
    public FieldState State { get; }

    /// <summary>The state class string for the field, as <see cref="FormidableCss.Compute"/> builds it.</summary>
    public string CssClass { get; }

    /// <summary>The field's current issues, any severity.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether the field has error-severity issues; bind it to the input's <c>aria-invalid</c>.</summary>
    public bool AriaInvalid { get; }

    /// <summary>The id of the field's message list while the field has issues, else <see langword="null"/>; bind it to the input's <c>aria-describedby</c>.</summary>
    /// <remarks>
    /// A single id, never a merged list: a control with a hint of its own composes both, hint
    /// first, as <c>aria-describedby="@($"my-hint {field.AriaDescribedBy}")"</c>.
    /// </remarks>
    public string? AriaDescribedBy { get; }

    /// <summary>How firmly the submit profile requires the field's value, as <see cref="IFormidableEngine.GetFieldRequirement"/> answers it; <see cref="FieldRequirement.Required"/> puts <c>aria-required</c> in <see cref="InputAttributes"/>.</summary>
    public FieldRequirement Requirement { get; }

    /// <summary>The attributes a custom input splats: <c>id</c>, <c>class</c>, and <c>aria-invalid</c>, <c>aria-describedby</c> and <c>aria-required</c> where each applies.</summary>
    /// <remarks>
    /// Splat them with <c>@attributes="field.InputAttributes"</c>. Wiring
    /// <see cref="NotifyChanged"/> stays the consumer's, because only the markup knows which
    /// event commits the value.
    /// </remarks>
    public IReadOnlyDictionary<string, object> InputAttributes { get; }

    /// <summary>Reports a committed value change from a custom input's change handler: the field is marked touched, engaged, and checked live from then on.</summary>
    public void NotifyChanged() => _engine.EditContext.NotifyFieldChanged(Field);

    /// <summary>Marks the field touched from a custom input's blur handler, without reporting a value change, so its classes update and no check runs.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);

    /// <summary>Adds <paramref name="item"/> to <paramref name="list"/> and, unless <paramref name="list"/> is a set that already holds it, reports the change as <see cref="NotifyChanged"/> does, so any check it starts reads the collection with the item in place.</summary>
    /// <typeparam name="TItem">The collection's item type.</typeparam>
    /// <param name="list">The collection this field names: a list, a set, or an entity's navigation collection.</param>
    /// <param name="item">The item to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="list"/> cannot grow, as an array cannot. The exception is the collection's own, and nothing is reported.</exception>
    /// <remarks>
    /// An <see cref="ISet{T}"/> reports only when its own <see cref="ISet{T}.Add(T)"/> returns
    /// <see langword="true"/>; any other collection always reports. Pass the collection
    /// <see cref="Field"/> names; nothing checks it. Another collection is edited all the same, and
    /// the change is still reported for this field.
    /// </remarks>
    public void AddItem<TItem>(ICollection<TItem> list, TItem item)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list is ISet<TItem> set)
        {
            if (!set.Add(item))
            {
                return;
            }
        }
        else
        {
            list.Add(item);
        }

        NotifyChanged();
    }

    /// <summary>Removes <paramref name="item"/> from <paramref name="list"/> and, when it was there, reports the change as <see cref="NotifyChanged"/> does, so any check it starts reads the collection without it.</summary>
    /// <typeparam name="TItem">The collection's item type.</typeparam>
    /// <param name="list">The collection this field names.</param>
    /// <param name="item">The item to remove.</param>
    /// <returns><see langword="true"/> when an item went; <see langword="false"/> when a list holds no match or the collection's own <see cref="ICollection{T}.Remove(T)"/> returned <see langword="false"/>, and then nothing is reported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="list"/> is a list that holds the item but cannot shrink, as an array cannot; or a collection that is not a list refuses its own <see cref="ICollection{T}.Remove(T)"/>, as a dictionary's <see cref="Dictionary{TKey, TValue}.Keys"/> does whether or not it holds the item. The exception is the collection's own, and nothing is reported.</exception>
    /// <remarks>
    /// In an <see cref="IList{T}"/>, the first element matching <paramref name="item"/> goes. When
    /// <typeparamref name="TItem"/> is a value type or <see cref="string"/>, an equal value matches;
    /// for any other <typeparamref name="TItem"/>, only the same instance does, so of two rows that
    /// compare equal, the one passed goes. Any other collection, such as a set, removes by its own
    /// <see cref="ICollection{T}.Remove(T)"/>. Pass the collection <see cref="Field"/> names;
    /// nothing checks it. Another collection is edited all the same, and the change is still
    /// reported for this field.
    /// </remarks>
    public bool RemoveItem<TItem>(ICollection<TItem> list, TItem item)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list is IList<TItem> indexed)
        {
            var index = IndexOfMatch(indexed, item);
            if (index < 0)
            {
                return false;
            }

            indexed.RemoveAt(index);
        }
        else if (!list.Remove(item))
        {
            return false;
        }

        NotifyChanged();
        return true;
    }

    /// <summary>Runs <paramref name="edit"/>, then reports the change as <see cref="NotifyChanged"/> does, so any check it starts reads the value the edit left.</summary>
    /// <param name="edit">The page's own edit to the value this field names, such as a reorder of its list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <remarks>An edit that throws reports nothing, and its exception reaches the caller.</remarks>
    public void Edit(Action edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        edit();
        NotifyChanged();
    }

    /// <summary>Runs <paramref name="edit"/> and, only when it returns <see langword="true"/>, reports the change as <see cref="NotifyChanged"/> does.</summary>
    /// <param name="edit">The page's own edit to the value this field names, returning whether it changed anything.</param>
    /// <returns>What <paramref name="edit"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <remarks>An edit that throws reports nothing, and its exception reaches the caller.</remarks>
    public bool Edit(Func<bool> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit())
        {
            return false;
        }

        NotifyChanged();
        return true;
    }

    /// <summary>Awaits <paramref name="edit"/>, then reports the change as <see cref="NotifyChanged"/> does, so any check it starts reads the value the edit left once it completed.</summary>
    /// <param name="edit">The page's own edit to the value this field names, which awaits before or while it changes the value.</param>
    /// <returns>A task that completes once the edit has completed and the change is reported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>, thrown by the call itself before any task exists.</exception>
    /// <remarks>An edit that faults or is cancelled reports nothing, and the returned task carries its exception or its cancellation.</remarks>
    public Task Edit(Func<Task> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return EditThenReportAsync(edit);
    }

    /// <summary>Awaits <paramref name="edit"/> and, only when it returns <see langword="true"/>, reports the change as <see cref="NotifyChanged"/> does.</summary>
    /// <param name="edit">The page's own edit to the value this field names, which awaits and returns whether it changed anything.</param>
    /// <returns>A task carrying what <paramref name="edit"/> returned, which completes once any report is made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>, thrown by the call itself before any task exists.</exception>
    /// <remarks>An edit that faults or is cancelled reports nothing, and the returned task carries its exception or its cancellation.</remarks>
    public Task<bool> Edit(Func<Task<bool>> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return EditThenReportAsync(edit);
    }

    // Both awaits keep the caller's context, because the report reaches the EditContext, which
    // belongs to the renderer's context.
    private async Task EditThenReportAsync(Func<Task> edit)
    {
        await edit();
        NotifyChanged();
    }

    private async Task<bool> EditThenReportAsync(Func<Task<bool>> edit)
    {
        if (!await edit())
        {
            return false;
        }

        NotifyChanged();
        return true;
    }

    // The rule keys on TItem, not on each item's own type. A value type or a string matches by
    // value: a boxed value is a new object on every call, and a tag typed into an input arrives
    // as a new string. Any other TItem (a class, an interface, object) matches by reference,
    // because a row's messages follow its instance: two rows that compare equal are still two
    // rows.
    private static int IndexOfMatch<TItem>(IList<TItem> list, TItem item)
    {
        if (typeof(TItem).IsValueType || typeof(TItem) == typeof(string))
        {
            var comparer = EqualityComparer<TItem>.Default;
            for (var i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item))
                {
                    return i;
                }
            }

            return -1;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
