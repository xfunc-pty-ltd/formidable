using FluentValidation;

namespace Formidable.Tests;

/// <summary>
/// Child validators written for a base type of what they validate, and the collection rule
/// <c>ForEach</c> builds. Each one's rules are read as the same rules written for the property's
/// own type would be, and a <c>ForEach</c> row files under the indexed path its failures carry.
/// </summary>
public class BaseTypeChildInspectionTests
{
    public class Address
    {
        public string? City { get; set; }
    }

    public class Phone
    {
        public string? Number { get; set; }
    }

    public class Person
    {
        public string? Name { get; set; }

        public Address Address { get; set; } = new();

        public List<string?> Tags { get; set; } = [];

        public List<Phone> Phones { get; set; } = [];
    }

    public sealed class Customer : Person
    {
        public string? Code { get; set; }
    }

    public sealed class Keeper : Person
    {
    }

    public sealed class Clinic
    {
        public string? Phone { get; set; }
    }

    public sealed class Vet
    {
        public Clinic Clinic { get; set; } = new();
    }

    public sealed class Collar
    {
        public string? Tag { get; set; }
    }

    public class Animal
    {
        public string? Name { get; set; }

        public List<Collar> Collars { get; set; } = [];

        public Vet Vet { get; set; } = new();

        public Keeper Keeper { get; set; } = new();
    }

    public sealed class Dog : Animal
    {
    }

    public sealed class Owner
    {
        public Dog Pet { get; set; } = new();
    }

    public sealed class Kennel
    {
        public List<Dog> Pets { get; set; } = [];
    }

    public sealed class Item
    {
        public string? Sku { get; set; }
    }

    public sealed class ListBasket
    {
        public List<Item> Items { get; set; } = [];

        public List<string?> Tags { get; set; } = [];
    }

    public sealed class ArrayBasket
    {
        public Item[] Items { get; set; } = [];

        public string?[] Tags { get; set; } = [];
    }

    public sealed class SequenceBasket
    {
        public IEnumerable<Item> Items { get; set; } = [];

        public IEnumerable<string?> Tags { get; set; } = [];
    }

    public sealed class AddressValidator : AbstractValidator<Address>
    {
        public AddressValidator() => RuleFor(a => a.City).NotEmpty();
    }

    public sealed class PhoneValidator : AbstractValidator<Phone>
    {
        public PhoneValidator() => RuleFor(p => p.Number).NotEmpty();
    }

    public sealed class PersonValidator : AbstractValidator<Person>
    {
        public PersonValidator()
        {
            RuleFor(p => p.Name).NotEmpty();
            RuleFor(p => p.Address).SetValidator(new AddressValidator());
        }
    }

    /// <summary>Includes a validator written for <c>Person</c>.</summary>
    public sealed class CustomerValidator : AbstractValidator<Customer>
    {
        public CustomerValidator()
        {
            RuleFor(c => c.Code).NotEmpty();
            Include(new PersonValidator());
        }
    }

    /// <summary>Hands the model itself to a validator written for <c>Person</c>, through a model-level rule.</summary>
    public sealed class ModelLevelCustomerValidator : AbstractValidator<Customer>
    {
        public ModelLevelCustomerValidator()
        {
            RuleFor(c => c.Code).NotEmpty();
            RuleFor(c => c).SetValidator(new PersonValidator());
        }
    }

    /// <summary>Two row-filtered presence rules, one on the rows themselves and one inside each row's child.</summary>
    public sealed class FilteredPersonValidator : AbstractValidator<Person>
    {
        public FilteredPersonValidator()
        {
            RuleForEach(p => p.Tags).Where(t => t != null).NotEmpty();
            RuleForEach(p => p.Phones).Where(p => p.Number != "n/a").SetValidator(new PhoneValidator());
        }
    }

    public sealed class FilteredCustomerValidator : AbstractValidator<Customer>
    {
        public FilteredCustomerValidator() => Include(new FilteredPersonValidator());
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

    public sealed class ClinicValidator : AbstractValidator<Clinic>
    {
        public ClinicValidator() => RuleFor(c => c.Phone).NotEmpty();
    }

    public sealed class VetValidator : AbstractValidator<Vet>
    {
        public VetValidator() => RuleFor(v => v.Clinic).SetValidator(new ClinicValidator());
    }

    /// <summary>A child two levels down, and a second validator written for a base type below the first.</summary>
    public sealed class AnimalCareValidator : AbstractValidator<Animal>
    {
        public AnimalCareValidator()
        {
            RuleFor(a => a.Vet).SetValidator(new VetValidator());
            RuleFor(a => a.Keeper).SetValidator(new PersonValidator());
        }
    }

    public sealed class OwnerCareValidator : AbstractValidator<Owner>
    {
        public OwnerCareValidator() => RuleFor(o => o.Pet).SetValidator(new AnimalCareValidator());
    }

    /// <summary>Each <c>Dog</c> row validated by a validator written for <c>Animal</c>.</summary>
    public sealed class KennelValidator : AbstractValidator<Kennel>
    {
        public KennelValidator() => RuleForEach(k => k.Pets).SetValidator(new AnimalValidator());
    }

    public sealed class ItemValidator : AbstractValidator<Item>
    {
        public ItemValidator() => RuleFor(i => i.Sku).NotEmpty();
    }

    public sealed class ListBasketValidator : AbstractValidator<ListBasket>
    {
        public ListBasketValidator()
        {
            RuleFor(b => b.Items).ForEach(i => i.SetValidator(new ItemValidator()));
            RuleFor(b => b.Tags).ForEach(t => t.NotEmpty());
        }
    }

    public sealed class ArrayBasketValidator : AbstractValidator<ArrayBasket>
    {
        public ArrayBasketValidator()
        {
            RuleFor(b => b.Items).ForEach(i => i.SetValidator(new ItemValidator()));
            RuleFor(b => b.Tags).ForEach(t => t.NotEmpty());
        }
    }

    public sealed class SequenceBasketValidator : AbstractValidator<SequenceBasket>
    {
        public SequenceBasketValidator()
        {
            RuleFor(b => b.Items).ForEach(i => i.SetValidator(new ItemValidator()));
            RuleFor(b => b.Tags).ForEach(t => t.NotEmpty());
        }
    }

    /// <summary>A collection rule with no property name on a root model that is itself the collection.</summary>
    public sealed class TagListValidator : AbstractValidator<List<string?>>
    {
        public TagListValidator() => RuleForEach(tags => tags).NotEmpty();
    }

    // The included validator's child is read under Person, the type its rules are written for.
    // The Include rule reaches PersonValidator through a child adaptor, so the child call is this
    // shape's site too. Mutation this breaks: walk a child under rule.TypeToValidate (Customer)
    // instead of the type its validator is written for, and Address.City goes unread.
    [Fact]
    public void A_child_inside_an_included_validator_written_for_a_base_type_is_read()
    {
        var validator = new FluentValidationModelValidator<Customer>(new CustomerValidator());

        Assert.Equal(
            ["Address.City", "Code", "Name"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Address.City", ValidationProfile.Submit));
    }

    // A row filter makes a presence rule conditional wherever it sits, including inside a
    // validator written for a base type. Mutation this breaks: walk a child under
    // rule.TypeToValidate (Customer), and the rules no longer read as collection rules, so the
    // tag rule files under Tags as a Required field and the row's child goes unread (no
    // Phones[].Number).
    [Fact]
    public void A_row_filter_inside_an_included_validator_written_for_a_base_type_is_conditional()
    {
        var validator = new FluentValidationModelValidator<Customer>(new FilteredCustomerValidator());

        Assert.Equal(
            ["Phones[].Number", "Tags[]"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.ConditionallyRequired,
            validator.GetFieldRequirement("Tags[0]", ValidationProfile.Submit));
        Assert.Equal(
            FieldRequirement.ConditionallyRequired,
            validator.GetFieldRequirement("Phones[0].Number", ValidationProfile.Submit));
    }

    // Each validator on the way down is read under its own type, so a child two levels below a
    // base-type child is read, and so is a second base-type child below the first. Mutation this
    // breaks: walk a child under rule.TypeToValidate, and nothing below Pet is declared.
    [Fact]
    public void A_child_two_levels_below_a_validator_written_for_a_base_type_is_read()
    {
        var validator = new FluentValidationModelValidator<Owner>(new OwnerCareValidator());

        Assert.Equal(
            ["Pet.Keeper.Address.City", "Pet.Keeper.Name", "Pet.Vet.Clinic.Phone"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Pet.Vet.Clinic.Phone", ValidationProfile.Submit));
    }

    // Mutation this breaks: walk a child under rule.TypeToValidate (Dog), and the collar rule's
    // adaptor, closed over Animal, fails the pair check, so Pets[].Collars[].Tag goes unread.
    [Fact]
    public void A_row_validator_written_for_a_base_type_reads_its_own_rows()
    {
        var validator = new FluentValidationModelValidator<Kennel>(new KennelValidator());

        Assert.Equal(
            ["Pets[].Collars[].Tag", "Pets[].Name"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Pets[0].Collars[0].Tag", ValidationProfile.Submit));
    }

    // ForEach hands the property a validator written for IEnumerable<T> that holds one collection
    // rule with no property name, and FluentValidation files that rule's failures under the
    // property's path with an index (Items[0].Sku, Tags[0]). Mutation this breaks: drop the [] a
    // collection rule with no property name adds, and the paths read Items.Sku and Tags, which no
    // failure carries. Walking a child under rule.TypeToValidate breaks the list and array rows too.
    [Theory]
    [InlineData("List<T>")]
    [InlineData("T[]")]
    [InlineData("IEnumerable<T>")]
    public void A_ForEach_files_its_children_under_the_indexed_path(string collection)
    {
        var (declared, item, tag) = collection switch
        {
            "List<T>" => Read(new FluentValidationModelValidator<ListBasket>(new ListBasketValidator())),
            "T[]" => Read(new FluentValidationModelValidator<ArrayBasket>(new ArrayBasketValidator())),
            _ => Read(new FluentValidationModelValidator<SequenceBasket>(new SequenceBasketValidator())),
        };

        Assert.Equal(["Items[].Sku", "Tags[]"], declared.Order(StringComparer.Ordinal));
        Assert.Equal(FieldRequirement.Required, item);
        Assert.Equal(FieldRequirement.Required, tag);

        static (IReadOnlySet<string> Declared, FieldRequirement Item, FieldRequirement Tag) Read<T>(
            FluentValidationModelValidator<T> validator) => (
                validator.GetDeclaredFieldPaths(ValidationProfile.Submit),
                validator.GetFieldRequirement("Items[0].Sku", ValidationProfile.Submit),
                validator.GetFieldRequirement("Tags[0]", ValidationProfile.Submit));
    }

    // A model-level rule hands the model to its child, which files at the root's own level and is
    // read under Person, the type its rules are written for. A pin of behaviour that already
    // holds. Mutation this breaks: walk a child under rule.TypeToValidate (Customer) instead of
    // the type its validator is written for, and Address.City goes unread.
    [Fact]
    public void A_child_a_model_level_rule_carries_is_read_under_the_type_it_is_written_for()
    {
        var validator = new FluentValidationModelValidator<Customer>(new ModelLevelCustomerValidator());

        Assert.Equal(
            ["Address.City", "Code", "Name"],
            validator.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
        Assert.Equal(
            FieldRequirement.Required,
            validator.GetFieldRequirement("Address.City", ValidationProfile.Submit));
    }

    // A pin: with no path travelled there is nothing to index, so such a rule on the root model
    // files its own components nowhere, as a rule with no name does. Mutation this breaks: index
    // the travelled path without checking it is empty, and the read throws.
    [Fact]
    public void A_collection_rule_with_no_name_on_the_root_model_files_nothing()
    {
        var validator = new FluentValidationModelValidator<List<string?>>(new TagListValidator());

        Assert.Empty(validator.GetDeclaredFieldPaths(ValidationProfile.Submit));
    }
}
