using FluentValidation;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Tests;

public class AsyncRuleMemoExtensionsTests
{
    private sealed class Handle
    {
        public string? Username { get; set; }
    }

    private sealed class HandleValidator : AbstractValidator<Handle>
    {
        private readonly AsyncRuleMemo<string, bool> _memo;

        public int Calls { get; private set; }

        public HandleValidator(TimeProvider time)
        {
            _memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);
            RuleFor(h => h.Username).MustAsyncMemoized(_memo, (_, _) =>
            {
                Calls++;
                return Task.FromResult(true);
            });
        }
    }

    [Fact]
    public async Task Validating_the_same_value_twice_runs_the_check_once()
    {
        var validator = new HandleValidator(new FakeTimeProvider());
        var handle = new Handle { Username = "tim" };

        await validator.ValidateAsync(handle);
        await validator.ValidateAsync(handle);

        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task A_null_value_is_checked_without_being_memoized()
    {
        var validator = new HandleValidator(new FakeTimeProvider());
        var handle = new Handle { Username = null };

        await validator.ValidateAsync(handle);
        await validator.ValidateAsync(handle);

        Assert.Equal(2, validator.Calls);
    }

    // TKey (notnull) also has to reach a property that can never be null in the first place - a
    // plain value type, not just a reference type. There is no null case to exercise here: the
    // point of this test is that the extension applies to this property shape at all.
    private sealed class Ticket
    {
        public int SeatNumber { get; set; }
    }

    private sealed class TicketValidator : AbstractValidator<Ticket>
    {
        private readonly AsyncRuleMemo<int, bool> _memo;

        public int Calls { get; private set; }

        public TicketValidator(TimeProvider time)
        {
            _memo = new AsyncRuleMemo<int, bool>(TimeSpan.FromSeconds(1), timeProvider: time);
            RuleFor(t => t.SeatNumber).MustAsyncMemoized(_memo, (_, _) =>
            {
                Calls++;
                return Task.FromResult(true);
            });
        }
    }

    [Fact]
    public async Task A_non_nullable_value_typed_property_memoizes_the_same_way()
    {
        var validator = new TicketValidator(new FakeTimeProvider());
        var ticket = new Ticket { SeatNumber = 12 };

        await validator.ValidateAsync(ticket);
        await validator.ValidateAsync(ticket);

        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public void The_rule_builder_extension_rejects_missing_arguments()
    {
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1));
        var validator = new InlineValidator<Handle>();
        var ruleBuilder = validator.RuleFor(h => h.Username);
        Func<string?, CancellationToken, Task<bool>> check = (_, _) => Task.FromResult(true);

        // Eagerly, not as a rule that fails later: the mistake is in the wiring, and a validator
        // built around it should never reach a validation pass at all.
        Assert.Throws<ArgumentNullException>(() =>
            AsyncRuleMemoExtensions.MustAsyncMemoized<Handle, string>(null!, memo, check));
        Assert.Throws<ArgumentNullException>(() => ruleBuilder.MustAsyncMemoized(null!, check));
        Assert.Throws<ArgumentNullException>(() => ruleBuilder.MustAsyncMemoized(memo, null!));
    }
}
