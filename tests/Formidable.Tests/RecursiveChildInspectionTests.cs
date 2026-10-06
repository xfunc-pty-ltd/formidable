using FluentValidation;

namespace Formidable.Tests;

/// <summary>
/// How rule inspection reads a validator that reaches its own type again: through instances held
/// since construction, which nest to a fixed depth, and through factories, which build a new
/// instance on every read and so never run out.
/// </summary>
public class RecursiveChildInspectionTests
{
    /// <summary>A model whose shape recurses through itself, as one child and as rows.</summary>
    private sealed class Node
    {
        public string Name { get; set; } = string.Empty;

        public Node? Child { get; set; }

        public List<Node> Children { get; set; } = [];
    }

    private static string[] DeclaredPaths(IValidator<Node> validator) =>
        [.. new FluentValidationModelValidator<Node>(validator)
            .GetDeclaredFieldPaths(ValidationProfile.Draft)
            .OrderBy(path => path, StringComparer.Ordinal)];

    /// <summary>Holds a validator of its own type for the child, built at construction, to a fixed depth.</summary>
    private sealed class HeldNodeValidator : AbstractValidator<Node>
    {
        public HeldNodeValidator(int depth)
        {
            RuleFor(n => n.Name).NotEmpty();
            if (depth > 0)
            {
                RuleFor(n => n.Child!).SetValidator(new HeldNodeValidator(depth - 1));
            }
        }
    }

    // A pin: each level is its own instance, held since construction, so the nesting ends where
    // the instances do and every level's rule is read.
    // Mutation: stop at any validator whose type is already on the way down, held or built. Only
    // Name is then read.
    [Fact]
    public void A_validator_nested_in_its_own_type_to_a_fixed_depth_is_read_to_that_depth()
    {
        Assert.Equal(
            ["Child.Child.Child.Name", "Child.Child.Name", "Child.Name", "Name"],
            DeclaredPaths(new HeldNodeValidator(3)));
    }

    /// <summary>Includes a validator of its own type, built by a factory on each read, under a condition that never holds.</summary>
    private sealed class SelfBuildingIncludeValidator : AbstractValidator<Node>
    {
        public SelfBuildingIncludeValidator()
        {
            RuleFor(n => n.Name).NotEmpty();
            When(_ => false, () => Include(_ => new SelfBuildingIncludeValidator()));
        }
    }

    // The factory hands back a new instance on every read, so meeting the same instance again
    // never stops the read. Validating ends, because the condition never holds; reading ignores
    // conditions.
    // Mutation: stop only at the same instance again. The read then recurses until the stack
    // overflows, which takes the test host down, so run that mutation alone with a filter.
    [Fact]
    public void An_include_that_builds_its_own_validator_type_is_read_once()
    {
        Assert.Equal(["Name"], DeclaredPaths(new SelfBuildingIncludeValidator()));
    }

    /// <summary>Hands each child row to a validator of its own type, built by a factory on each read.</summary>
    private sealed class SelfBuildingRowValidator : AbstractValidator<Node>
    {
        public SelfBuildingRowValidator()
        {
            RuleFor(n => n.Name).NotEmpty();
            RuleForEach(n => n.Children).SetValidator((_, _) => new SelfBuildingRowValidator());
        }
    }

    // The factory twin of a validator that hands its own rows to itself, which is read once.
    // Mutation: stop only at the same instance again. The read then overflows the stack (run it
    // alone with a filter).
    [Fact]
    public void A_row_validator_a_factory_builds_of_its_own_type_is_read_once()
    {
        Assert.Equal(["Name"], DeclaredPaths(new SelfBuildingRowValidator()));
    }

    private sealed class BuildsSecondValidator : AbstractValidator<Node>
    {
        public BuildsSecondValidator()
        {
            RuleFor(n => n.Name).NotEmpty();
            RuleFor(n => n.Child!).SetValidator(_ => new BuildsFirstValidator());
        }
    }

    private sealed class BuildsFirstValidator : AbstractValidator<Node>
    {
        public BuildsFirstValidator()
        {
            RuleFor(n => n.Name).NotEmpty();
            RuleForEach(n => n.Children).SetValidator((_, _) => new BuildsSecondValidator());
        }
    }

    // Two validators whose factories build each other: the read stops when it would build the
    // first type again, one level below where it started.
    // Mutation: stop only at the same instance again. The read then overflows the stack (run it
    // alone with a filter).
    [Fact]
    public void Two_validators_whose_factories_build_each_other_are_each_read_once()
    {
        Assert.Equal(["Child.Name", "Name"], DeclaredPaths(new BuildsSecondValidator()));
    }

    private sealed class LeafValidator : AbstractValidator<Node>
    {
        public LeafValidator() => RuleFor(n => n.Name).NotEmpty();
    }

    /// <summary>Two siblings of one type, each built by a factory: one for the child, one for each row.</summary>
    private sealed class FactorySiblingsValidator : AbstractValidator<Node>
    {
        public FactorySiblingsValidator()
        {
            RuleFor(n => n.Child!).SetValidator(_ => new LeafValidator());
            RuleForEach(n => n.Children).SetValidator((_, _) => new LeafValidator());
        }
    }

    // A pin: a validator stops the read only while it is on the way down, so a sibling of the
    // same type, built by its own factory, is read too.
    // Mutation: keep each validator on the way down after its own read returns. The rows' Name
    // is then never read.
    [Fact]
    public void Two_factory_built_siblings_of_one_type_are_each_read()
    {
        Assert.Equal(["Child.Name", "Children[].Name"], DeclaredPaths(new FactorySiblingsValidator()));
    }
}
