using FluentValidation;

namespace Formidable.Sample.Shared;

public class Roster
{
    public List<Team> Teams { get; set; } = [];
}

public class Team
{
    public string Name { get; set; } = string.Empty;
    public List<Member> Members { get; set; } = [];
}

public class Member
{
    public string Alias { get; set; } = string.Empty;
}

public class RosterValidator : DraftSubmitValidator<Roster>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(r => r.Teams).NotEmpty().WithMessage("Add at least one team");
        RuleForEach(r => r.Teams).ChildRules(team =>
        {
            team.RuleFor(t => t.Name).NotEmpty().WithMessage("Team name is required");
            team.RuleFor(t => t.Members).NotEmpty().WithMessage("Every team needs at least one member");
            team.RuleForEach(t => t.Members).ChildRules(member =>
                member.RuleFor(m => m.Alias).NotEmpty().WithMessage("Alias is required"));
        });
    }
}
