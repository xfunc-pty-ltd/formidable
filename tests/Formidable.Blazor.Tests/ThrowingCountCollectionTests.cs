using System.Collections.Immutable;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A collection whose own count throws (a default <see cref="ImmutableArray{T}"/>, whose
/// <c>ICollection.Count</c> refuses an uninitialized instance) counts no rows, as a collection that
/// refuses a walk counts none, and the rest of the form keeps its answers: the other fields keep
/// their required marks, a submit's "checking" stays on the fields the validator declares, and a
/// load lands. FluentValidation itself runs clean over the model, because the row rule's condition
/// skips a default array.
/// </summary>
// The mutation that must break all three: read ICollection.Count outside the refusal handling
// again, and the throw escapes the row count (every required mark goes, a submit lights every
// field, and the load throws). The control rows use an initialized empty array, which counts zero
// without throwing, so each pin also shows the default array is the shape under test.
public class ThrowingCountCollectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_collection_whose_count_throws_keeps_the_other_fields_required_marks(bool defaultArray)
    {
        var model = new Order { Tags = defaultArray ? default : [] };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new OrderValidator());

        Assert.Equal(defaultArray, model.Tags.IsDefault);
        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(new FieldIdentifier(model, nameof(Order.Name))));
        Assert.Equal(FieldRequirement.NotRequired, engine.GetFieldRequirement(new FieldIdentifier(model, nameof(Order.Notes))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_submit_beside_a_collection_whose_count_throws_marks_only_the_declared_fields_checking(bool defaultArray)
    {
        var model = new Order { Name = "ok", Tags = defaultArray ? default : [] };
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new OrderValidator(gate.Task));

        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsValidating, "the async rule should hold the submit open");
        Assert.True(engine.GetFieldState(new FieldIdentifier(model, nameof(Order.Name))).IsValidating);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model, nameof(Order.Notes))).IsValidating);

        gate.SetResult(true);
        Assert.True((await submit).CanProceed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_load_beside_a_collection_whose_count_throws_lands(bool defaultArray)
    {
        var model = new Order { Name = "toolong", Notes = "kept", Tags = defaultArray ? default : [] };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new OrderValidator());
        var name = new FieldIdentifier(model, nameof(Order.Name));

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(engine.GetFieldState(name).IsTouched);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model, nameof(Order.Notes))).IsTouched);
        Assert.Equal([NameTooLong], editContext.GetValidationMessages(name));
    }

    private const string NameTooLong = "Name too long";

    public sealed class Order
    {
        public string? Name { get; set; }

        public string? Notes { get; set; }

        public ImmutableArray<string> Tags { get; init; }
    }

    private sealed class OrderValidator : AbstractValidator<Order>
    {
        public OrderValidator(Task<bool>? nameCheck = null)
        {
            RuleFor(m => m.Name).NotEmpty().MaximumLength(5).WithMessage(NameTooLong);
            RuleForEach(m => m.Tags).NotEmpty().When(m => !m.Tags.IsDefault);
            if (nameCheck is not null)
            {
                RuleFor(m => m.Name).MustAsync(async (_, ct) => await nameCheck.WaitAsync(ct));
            }
        }
    }

    private static FormidableEngine<Order> Build(Order model, EditContext editContext, IValidator<Order> validator) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<Order>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
}
