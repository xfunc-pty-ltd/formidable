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

    private void HandleValid() => _status = "Submitted — every row passed.";

    private static void MoveUp<T>(List<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index > 0)
        {
            list.RemoveAt(index);
            list.Insert(index - 1, item);
        }
    }
}
