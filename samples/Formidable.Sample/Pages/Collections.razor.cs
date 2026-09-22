using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Forms;

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

    // A collection rule fails against the list, not against any one input, so its summary entry
    // has nothing to focus unless some element carries the collection's id: the container holding
    // every team for the roster's own rule, and each team's box for that team's member rule. Both
    // ids are derived from the model, so no state is added to keep them in step.
    private string TeamsId => FormidableFieldId.For(new FieldIdentifier(_roster, nameof(Roster.Teams)));

    private static string MembersId(Team team) =>
        FormidableFieldId.For(new FieldIdentifier(team, nameof(Team.Members)));

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
