using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A server reply is read whole, and its paths resolved, before the engine writes anything, so a
/// reply that throws as it is read, or a path the introspector throws on, leaves the engine as it
/// was: the earlier reply still stands and <see cref="FormidableEngine{TModel}.HasSubmitted"/> is
/// unchanged.
/// </summary>
// Mutation that must break both: read and resolve the reply after the writes that begin an apply
// (HasSubmitted, the fault message, the server stores), and the earlier reply is gone.
public class ServerReplyThatThrowsTests
{
    private const string Taken = "Taken";

    [Fact]
    public void A_reply_that_throws_as_it_is_read_changes_nothing()
    {
        var model = new Booking();
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new ReflectionModelIntrospector());

        Assert.Throws<InvalidOperationException>(() => engine.ApplyServerIssues(BreaksOff()));
        Assert.False(engine.HasSubmitted);

        engine.ApplyServerIssues([new ValidationIssue(nameof(Booking.Name), Taken)]);
        var shown = Shown(engine, editContext);

        Assert.Throws<InvalidOperationException>(() => engine.ApplyServerIssues(BreaksOff()));

        Assert.Contains("view: Name Taken", shown);
        Assert.Equal(shown, Shown(engine, editContext));
    }

    [Fact]
    public void A_reply_whose_path_the_introspector_throws_on_changes_nothing()
    {
        var model = new Booking();
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new ThrowsOn("Boom"));

        Assert.Throws<InvalidOperationException>(() => engine.ApplyServerIssues([new ValidationIssue("Boom", "x")]));
        Assert.False(engine.HasSubmitted);

        engine.ApplyServerIssues([new ValidationIssue(nameof(Booking.Name), Taken)]);
        var shown = Shown(engine, editContext);

        Assert.Throws<InvalidOperationException>(() => engine.ApplyServerIssues(
            [new ValidationIssue(nameof(Booking.Notes), "Other"), new ValidationIssue("Boom", "x")]));

        Assert.Contains("view: Name Taken", shown);
        Assert.Equal(shown, Shown(engine, editContext));
    }

    private static IEnumerable<ValidationIssue> BreaksOff()
    {
        yield return new ValidationIssue(nameof(Booking.Notes), "Other");
        throw new InvalidOperationException("The reply breaks off.");
    }

    // What the engine's views and the EditContext's store show, in a fixed order.
    private static List<string> Shown(FormidableEngine<Booking> engine, EditContext editContext) =>
    [
        .. engine.GetVisibleIssues().Select(visible => $"view: {visible.Issue.Path} {visible.Issue.Message}").Order(),
        .. editContext.GetValidationMessages().Select(message => $"store: {message}").Order(),
        $"submitted: {engine.HasSubmitted}",
    ];

    private static FormidableEngine<Booking> Build(Booking model, EditContext editContext, IModelIntrospector introspector) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<Booking>(new BookingValidator()),
            introspector,
            new FormidableOptions(),
            new FakeTimeProvider());

    public sealed class Booking
    {
        public string? Name { get; set; } = "ok";

        public string? Notes { get; set; }
    }

    private sealed class BookingValidator : AbstractValidator<Booking>
    {
        public BookingValidator() => RuleFor(m => m.Name).NotEmpty();
    }

    /// <summary>The reflection introspector, throwing for one path.</summary>
    private sealed class ThrowsOn(string path) : IModelIntrospector
    {
        private readonly ReflectionModelIntrospector _inner = new();

        public ResolvedField Resolve(object rootModel, string propertyPath) =>
            propertyPath == path
                ? throw new InvalidOperationException("The introspector cannot resolve this path.")
                : _inner.Resolve(rootModel, propertyPath);

        public bool TryReadValue(object owner, string propertyName, out object? value, out Type? declaredType) =>
            _inner.TryReadValue(owner, propertyName, out value, out declaredType);
    }
}
