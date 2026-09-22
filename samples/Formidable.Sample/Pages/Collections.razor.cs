using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class Collections
{
    private readonly Roster _roster = new()
    {
        Teams =
        [
            new Team { Name = "Alpha", Members = [new Member { Alias = "ace" }, new Member()] },
            new Team { Members = [new Member()] }
        ]
    };

    private string _status = string.Empty;

    // A row list that loses its @key can misfile a message onto the wrong row without a single
    // symptom on screen - the exact mistake this page exists to teach against. VerifyRowKeys
    // catches it at the source: turned on, a field-bound component throws the moment its
    // accessor no longer names the row it registered, rather than silently rendering someone
    // else's row. A real app would typically gate this to Development builds only; it stays on
    // unconditionally here since the page's whole point is demonstrating the guard it protects.
    private readonly FormidableOptions _options = new() { VerifyRowKeys = true };

    private void HandleValid() => _status = "Submitted — every row passed.";

    // Add, Remove and MoveUp all mutate a list directly, and an edit the engine never hears
    // about is one no pass starts for - so each handler notifies the field context afterward.
    // NotifyChanged() engages the field, and a live pass answers only the fields such
    // notifications have engaged. A rule that
    // starts or stops failing because of the edit, "every team needs at least one member" going
    // red the moment the last one leaves, needs a fresh pass to say so; no prune can invent an
    // issue no pass produced. MoveUp notifies too, for the same contract, even though reordering
    // doesn't change what any rule here has to say.
    private static void AddItem<T>(List<T> list, T item, FormidableFieldContext field)
    {
        list.Add(item);
        field.NotifyChanged();
    }

    private static void RemoveItem<T>(List<T> list, T item, FormidableFieldContext field)
    {
        list.Remove(item);
        field.NotifyChanged();
    }

    private static void MoveUp<T>(List<T> list, T item, FormidableFieldContext field)
    {
        var index = list.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        list.RemoveAt(index);
        list.Insert(index - 1, item);
        field.NotifyChanged();
    }
}
