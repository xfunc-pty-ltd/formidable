using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The recipe for a list that needs items, measured. <c>NotEmpty()</c> on a list is a presence
/// rule the form can read, so the list is marked required and fails with FluentValidation's own
/// text. A count above one is the consumer-owned <see cref="MinimumCountExtensions.MinimumCount"/>
/// over <c>Must</c>, a predicate the form cannot read as a demand. <c>NotNull()</c> in front of
/// the count is a presence rule too, so that chain marks the list and fails a missing one once.
/// The recipe quotes the rule in each of the first two validators below and the extension
/// byte-true, and names the third validator's chain inline, so these tests hold its claims still.
/// </summary>
public class CollectionCountRecipeTests : BunitContext
{
    public CollectionCountRecipeTests() => Services.AddFormidable();

    // NotEmpty() is one of the two rules the form reads as a presence demand, and a list is read
    // the way a string is. The engine's answer and the drawn marker are asserted together, so a
    // demand nothing draws cannot pass. Mutation that must break this: dropping
    // INotEmptyValidator from what the adapter counts as presence (IsPresenceComponent).
    [Fact]
    public void NotEmpty_on_a_list_marks_it_required()
    {
        var crew = new Crew();
        var cut = RenderCrewForm(crew, new AtLeastOneMemberValidator());

        Assert.Equal(FieldRequirement.Required, RequirementOf(cut, crew));
        Assert.Equal("*", cut.Find("span.formidable-required").TextContent);
    }

    // The text an empty list fails with when its rule names no message of its own, which the
    // recipe quotes. Mutation that must break this: the adapter reporting the failure's error
    // code where it reports its message.
    [Fact]
    public async Task NotEmpty_on_an_empty_list_reports_its_message()
    {
        var validator = new FluentValidationModelValidator<Crew>(new AtLeastOneMemberValidator());

        var report = await validator.ValidateAsync(new Crew(), ValidationProfile.Submit);

        var issue = Assert.Single(report.Errors);
        Assert.Equal(nameof(Crew.Members), issue.Path);
        Assert.Equal("'Members' must not be empty.", issue.Message);
    }

    // A count over Must is a predicate, and a predicate is indistinguishable from any other, so
    // the list stays unmarked. The same pair of asserts as the NotEmpty() test, over the same
    // form, so the difference is the rule rather than the surface. Mutation that must break
    // this: counting every predicate as a presence demand in IsPresenceComponent.
    [Fact]
    public void MinimumCount_over_Must_is_not_read_as_required()
    {
        var crew = new Crew();
        var cut = RenderCrewForm(crew, new AtLeastTwoMembersValidator());

        Assert.Equal(FieldRequirement.NotRequired, RequirementOf(cut, crew));
        Assert.Empty(cut.FindAll("span.formidable-required"));
    }

    // NotNull() is the other rule the form reads as a presence demand, so the chain the recipe
    // gives a list that can arrive as null marks it, where the count alone does not. The same pair
    // of asserts as the NotEmpty() test. Mutation that must break this: dropping INotNullValidator
    // from what the adapter counts as presence (IsPresenceComponent).
    [Fact]
    public void NotNull_in_front_of_the_count_marks_the_list_required()
    {
        var crew = new OptionalCrew();
        var cut = RenderListForm(crew, () => crew.Members, new OptionalCrewValidator());

        Assert.Equal(FieldRequirement.Required, RequirementOf(cut, crew));
        Assert.Equal("*", cut.Find("span.formidable-required").TextContent);
    }

    // The recipe's way back to a marker for a counted list: the override declares the list
    // required, and the marker draws from that declaration. Mutation that must break this:
    // ignoring RequiredOverride in the engine's GetFieldRequirement.
    [Fact]
    public void RequiredOverride_marks_a_list_counted_over_Must()
    {
        var crew = new Crew();
        var options = new FormidableOptions
        {
            RequiredOverride = field => field.FieldName == nameof(Crew.Members)
                ? FieldRequirement.Required
                : null,
        };
        var cut = RenderCrewForm(crew, new AtLeastTwoMembersValidator(), options);

        Assert.Equal(FieldRequirement.Required, RequirementOf(cut, crew));
        Assert.Equal("*", cut.Find("span.formidable-required").TextContent);
    }

    // The boundary is inclusive, and the message names the number. Mutations that must break
    // this: the extension's comparison changed to >, and the minimum dropped from its message.
    [Fact]
    public async Task MinimumCount_passes_at_the_minimum_and_fails_below_it()
    {
        var validator = new FluentValidationModelValidator<Crew>(new AtLeastTwoMembersValidator());

        var atMinimum = await validator.ValidateAsync(
            new Crew { Members = [new(), new()] }, ValidationProfile.Submit);
        var below = await validator.ValidateAsync(
            new Crew { Members = [new()] }, ValidationProfile.Submit);

        Assert.Empty(atMinimum.Errors);
        var issue = Assert.Single(below.Errors);
        Assert.Equal(nameof(Crew.Members), issue.Path);
        Assert.Equal("'Members' must contain at least 2 items.", issue.Message);
    }

    // A null list is not a count. It passes MinimumCount and is left to NotNull(), so the chain
    // the recipe suggests reports one failure for it, NotNull()'s, rather than two. The list
    // property is nullable, the shape a model reaching for NotNull() declares. Mutation that
    // must break this: failing a null list in the extension (list is not null && ...).
    [Fact]
    public async Task MinimumCount_passes_a_null_list()
    {
        var validator = new FluentValidationModelValidator<OptionalCrew>(new OptionalCrewValidator());

        var report = await validator.ValidateAsync(
            new OptionalCrew { Members = null }, ValidationProfile.Submit);

        var issue = Assert.Single(report.Errors);
        Assert.Equal("NotNullValidator", issue.Code);
    }

    // Both models name their list Members, so one field name serves either.
    private static FieldRequirement RequirementOf<TModel>(IRenderedComponent<FormidableForm<TModel>> cut, TModel model)
        where TModel : class =>
        cut.Instance.Engine!.GetFieldRequirement(new FieldIdentifier(model, nameof(Crew.Members)));

    private IRenderedComponent<FormidableForm<Crew>> RenderCrewForm(
        Crew crew, IValidator<Crew> validator, FormidableOptions? options = null) =>
        RenderListForm(crew, () => crew.Members, validator, options);

    /// <summary>
    /// A form holding one marker for the list, the way a page puts one beside a list's heading.
    /// Nothing else renders, so any marker found is the list's.
    /// </summary>
    private IRenderedComponent<FormidableForm<TModel>> RenderListForm<TModel>(
        TModel model,
        Expression<Func<List<CrewMember>?>> list,
        IValidator<TModel> validator,
        FormidableOptions? options = null)
        where TModel : class
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<TModel>(validator));
            builder.AddComponentParameter(3, "Options", options);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableRequiredIndicator<List<CrewMember>?>>(0);
                inner.AddComponentParameter(1, "For", list);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        return cut.FindComponent<FormidableForm<TModel>>();
    }
}

public sealed class Crew
{
    public List<CrewMember> Members { get; set; } = [];
}

public sealed class CrewMember
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>A list whose absence is a separate question from its size.</summary>
public sealed class OptionalCrew
{
    public List<CrewMember>? Members { get; set; }
}

/// <summary>One or more, with the rule FluentValidation already has.</summary>
public sealed class AtLeastOneMemberValidator : AbstractValidator<Crew>
{
    public AtLeastOneMemberValidator()
    {
        RuleFor(c => c.Members).NotEmpty();
    }
}

/// <summary>Two or more, with the consumer-owned count.</summary>
public sealed class AtLeastTwoMembersValidator : AbstractValidator<Crew>
{
    public AtLeastTwoMembersValidator()
    {
        RuleFor(c => c.Members).MinimumCount(2);
    }
}

/// <summary>A list that must be present, and hold two or more once it is.</summary>
public sealed class OptionalCrewValidator : AbstractValidator<OptionalCrew>
{
    public OptionalCrewValidator()
    {
        RuleFor(c => c.Members).NotNull().MinimumCount(2);
    }
}
