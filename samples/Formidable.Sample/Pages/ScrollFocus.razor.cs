using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class ScrollFocus
{
    private const int TeamCount = 12;
    private const int MembersPerTeam = 3;

    private readonly Roster _roster = BuildRoster();

    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — every row reachable, every row valid.";

    // Static roster: twelve teams of three members, seeded deterministically so the
    // summary is long enough to make scroll distance the point. Roughly every 4th
    // alias is empty ((t * 3 + m) % 4 == 3); team names use the same formula at m = 0,
    // so both patterns land on the same quarter of teams. The residue 3 (rather than 0)
    // is deliberate: it puts the very last member row (t = 11, m = 2) in the empty set,
    // so the summary's last entry is always the form's last row - the page's "click the
    // last entry" walkthrough needs that to genuinely reach the bottom.
    private static Roster BuildRoster()
    {
        var teams = new List<Team>(TeamCount);
        for (var t = 0; t < TeamCount; t++)
        {
            var members = new List<Member>(MembersPerTeam);
            for (var m = 0; m < MembersPerTeam; m++)
            {
                members.Add(new Member
                {
                    Alias = (t * 3 + m) % 4 == 3 ? string.Empty : $"member-{t + 1}-{m + 1}"
                });
            }

            teams.Add(new Team
            {
                Name = (t * 3) % 4 == 3 ? string.Empty : $"Team {t + 1}",
                Members = members
            });
        }

        return new Roster { Teams = teams };
    }
}
