using FluentValidation;
using FluentValidation.Validators;

namespace Formidable.Tests;

/// <summary>
/// Which child validators the rule reading reaches. One attached through a subclass of
/// FluentValidation's child adaptor is read like one attached the usual way. One met below a child
/// validator written for a base type is read too, because the rules around it are read under the
/// base type they were written for.
/// </summary>
public class ChildAdaptorReadingTests
{
    public sealed class Address
    {
        public string? Street { get; set; }
    }

    public sealed class Customer
    {
        public Address Address { get; set; } = new();
    }

    public sealed class AddressValidator : AbstractValidator<Address>
    {
        public AddressValidator() => RuleFor(a => a.Street).NotEmpty();
    }

    /// <summary>A subclass of the adaptor that changes nothing the reading uses.</summary>
    public sealed class RenamedAdaptor(IValidator<Address> validator)
        : ChildValidatorAdaptor<Customer, Address>(validator, validator.GetType());

    public sealed class CustomerValidator : AbstractValidator<Customer>
    {
        public CustomerValidator() =>
            RuleFor(c => c.Address).SetValidator(new RenamedAdaptor(new AddressValidator()));
    }

    // Mutation this breaks: look for the adaptor in the component's own type only, not in its
    // base types, and the child behind the subclass goes unread.
    [Fact]
    public void A_child_attached_through_a_subclass_of_the_adaptor_is_read()
    {
        var validator = new FluentValidationModelValidator<Customer>(new CustomerValidator());

        Assert.Equal(["Address.Street"], validator.GetDeclaredFieldPaths(ValidationProfile.Submit));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Address.Street", ValidationProfile.Submit));
    }

    public class Animal
    {
        public string? Name { get; set; }

        public List<Collar> Collars { get; set; } = [];
    }

    public sealed class Dog : Animal
    {
    }

    public sealed class Collar
    {
        public string? Tag { get; set; }
    }

    public sealed class Owner
    {
        public Dog Pet { get; set; } = new();
    }

    public sealed class CollarValidator : AbstractValidator<Collar>
    {
        public CollarValidator() => RuleFor(c => c.Tag).NotEmpty();
    }

    public sealed class AnimalValidator : AbstractValidator<Animal>
    {
        public AnimalValidator()
        {
            RuleFor(a => a.Name).NotEmpty();
            RuleForEach(a => a.Collars).SetValidator(new CollarValidator());
        }
    }

    /// <summary>A <c>Dog</c> property validated by a validator written for <c>Animal</c>.</summary>
    public sealed class OwnerValidator : AbstractValidator<Owner>
    {
        public OwnerValidator() => RuleFor(o => o.Pet).SetValidator(new AnimalValidator());
    }

    // The base-typed validator's rules are read under Animal, the type they are written for, so
    // the collection rule among them reads as one and the collar tag behind it is filed under the
    // path its failures carry. Mutation this breaks: walk a child under rule.TypeToValidate (Dog)
    // instead of the type its validator is written for, and Pet.Collars[].Tag goes unread.
    [Fact]
    public void A_child_below_a_validator_written_for_a_base_type_is_read()
    {
        var validator = new FluentValidationModelValidator<Owner>(new OwnerValidator());

        Assert.Equal(
            ["Pet.Collars[].Tag", "Pet.Name"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Pet.Collars[0].Tag", ValidationProfile.Submit));
    }
}
