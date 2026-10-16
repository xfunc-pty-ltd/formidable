using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Each publish says which fields' views moved (state or messages) and whether something no
/// field owns moved (order, validity, a check running, a submit answered), so a subscriber can
/// skip a change that names none of its fields. The engine computes the set itself; nothing is
/// handed in. An engine with no root raises directly, which is enough to read the arguments.
/// </summary>
public class StateChangedArgsTests
{
    private readonly EngineOrder _order = new() { Customer = new EngineCustomer() };
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly List<FormidableStateChangedEventArgs> _events = [];

    public StateChangedArgsTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order,
            _editContext,
            new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan },
            new FakeTimeProvider());
        _engine.StateChanged += (_, e) => _events.Add(e);
    }

    // Fails today: every publish carries Empty, whose set is null.
    [Fact]
    public async Task A_committed_edit_names_the_edited_field_and_no_other()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));
        _engine.Registry.Register(description);
        _engine.Registry.Register(customerName);

        _editContext.NotifyFieldChanged(description);
        await SettleAsync();

        Assert.True(_events.Count >= 3, $"expected the touch, the begin and the end; saw {_events.Count}");
        Assert.All(_events, e =>
        {
            Assert.NotNull(e.ChangedFields);
            Assert.True(e.Includes(description));
            Assert.False(e.Includes(customerName));
            Assert.DoesNotContain(_engine.ModelLevelField, e.ChangedFields!);
        });
    }

    [Fact]
    public void A_changed_order_is_a_form_level_change_and_names_no_field()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);

        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 });

        var e = Assert.Single(_events);
        Assert.True(e.FormChanged);
        Assert.Empty(e.ChangedFields!);
    }

    [Fact]
    public async Task A_submits_begin_and_end_are_form_level_changes()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);

        await _engine.ValidateForSubmitAsync();

        Assert.Contains(_events, e => e.FormChanged);   // IsSubmitting rose, then fell and HasSubmitted rose
        Assert.Contains(_events, e => e.Includes(description));   // the submit's errors landed on it
        Assert.Contains(_events, e => e.Includes(_engine.ModelLevelField));   // "The order is incomplete"
    }

    [Fact]
    public void The_empty_instance_and_a_null_set_mean_every_field()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));

        Assert.True(FormidableStateChangedEventArgs.Empty.Everything);
        Assert.True(FormidableStateChangedEventArgs.Empty.Includes(description));
        Assert.False(FormidableStateChangedEventArgs.Empty.FormChanged);
        Assert.False(new FormidableStateChangedEventArgs { ChangedFields = new HashSet<FieldIdentifier>() }.Includes(description));
    }

    // Review focus 5: the diff reads GetIssues, which under EngagedAndVisible consults
    // DisclosureOverride for an engaged field nothing registers. Its throw must land where a
    // component's render would have sent it, which is the publish's caller: here the test's own
    // call, since there is no root. Under a root the live pass's caller is the engine's background
    // observer, which hands it to the boundary (FormidableEngineHandlerThrowTests). The override
    // is consulted only once the field has a live issue, so a first check runs with a benign
    // override, and the throwing one goes in before a later publish. Options are read at each
    // use, which is what lets the test swap it. Confirm against LiveViewOf before relying on the
    // unregistered-field condition; register nothing for the field in this test.
    [Fact]
    public async Task A_disclosure_override_that_throws_during_the_diff_reaches_the_publishs_caller()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var options = new FormidableOptions
        {
            RefreshDebounce = Timeout.InfiniteTimeSpan,
            LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible,
            DisclosureOverride = _ => true,
        };
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        editContext.NotifyFieldChanged(description);
        for (var i = 0; i < 50 && engine.IsValidating; i++)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(engine.GetIssues(description));

        options.DisclosureOverride = _ => throw new InvalidOperationException("override");
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 }));

        Assert.Equal("override", thrown.Message);
    }

    private async Task SettleAsync()
    {
        for (var i = 0; i < 50 && _engine.IsValidating; i++)
        {
            await Task.Delay(10);
        }

        await Task.Yield();
    }
}
