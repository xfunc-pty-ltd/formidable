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

    // An edit the engine never hears about starts no live check. The Add and Remove buttons go
    // through the field context's AddItem and RemoveItem, which edit the list and then notify.
    // A reorder has no such call, so the Move up buttons hand MoveUp to the field context's Edit,
    // which runs it and notifies only when it returns true: the first row has nowhere to go, and
    // its Move up changes nothing and notifies nothing. The notification comes even though
    // reordering doesn't change what any rule here has to say. It engages the field, and the live
    // check it starts answers only the fields such notifications have engaged. A rule that starts
    // or stops failing because of the edit, "every team needs at least one member" going red the
    // moment the last one leaves, needs a fresh check to say so; no prune can invent an issue no
    // check produced.
    private static bool MoveUp<T>(List<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index <= 0)
        {
            return false;
        }

        list.RemoveAt(index);
        list.Insert(index - 1, item);
        return true;
    }
}
