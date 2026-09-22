using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the payload contract of the engine's two events: every raise hands the engine itself as
/// the sender, and the fault event's arguments carry the exception INSTANCE the pass failed with
/// — never a copy or a wrap — so a subscriber can correlate it with anything else that observed
/// the same failure. The fault event is pinned once per raise path, because the paths raise
/// independently: a live pass's fault reports through the engine's fault policy, while the
/// validity probe's raises alone.
/// </summary>
public class FormValidationEngineEventPayloadTests
{
    [Fact]
    public void StateChanged_hands_the_engine_as_sender_with_non_null_args()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator(), new FormidableOptions());

        object? sender = null;
        FormidableStateChangedEventArgs? args = null;
        var raised = 0;
        engine.StateChanged += (s, e) =>
        {
            sender = s;
            args = e;
            raised++;
        };

        // MarkTouched raises synchronously on a first touch, with no pass alongside to blur
        // which raise is being observed.
        engine.MarkTouched(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        Assert.Equal(1, raised);
        Assert.Same(engine, sender);
        Assert.NotNull(args);
    }

    [Fact]
    public async Task Live_fault_hands_the_engine_as_sender_and_the_thrown_exception_instance()
    {
        var order = new EngineOrder();
        var validator = new CapturedThrowValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        object? sender = null;
        FormidableValidationFaultedEventArgs? args = null;
        engine.ValidationFaulted += (s, e) =>
        {
            sender = s;
            args = e;
        };

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.Same(engine, sender);
        Assert.NotNull(args);
        Assert.Same(validator.Boom, args.Exception);
    }

    [Fact]
    public async Task Probe_fault_hands_the_engine_as_sender_and_the_thrown_exception_instance()
    {
        var order = new EngineOrder();
        var validator = new CapturedThrowValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                TrackFormValidity = true,
                // The probe is the only thing that can fault below: this live profile selects the
                // "Submit" ruleset — registered on the validator but empty there — and excludes
                // default rules, so the live pass runs zero rules and never reaches the throwing
                // one, which lives in the default ruleset only the probe still runs.
                LiveProfile = ValidationProfile.Named(
                    "EmptyLive", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName),
            },
            new FakeTimeProvider());

        // The constructor's own initial probe faults before anything can subscribe; let it run
        // out so the raise observed below is attributable to the edit-triggered probe alone.
        await Task.Yield();

        object? sender = null;
        FormidableValidationFaultedEventArgs? args = null;
        engine.ValidationFaulted += (s, e) =>
        {
            sender = s;
            args = e;
        };

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.Same(engine, sender);
        Assert.NotNull(args);
        Assert.Same(validator.Boom, args.Exception);
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(
        EngineOrder order,
        FluentValidation.IValidator<EngineOrder> validator,
        FormidableOptions options) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    /// <summary>
    /// Throws one captured exception instance from a default-ruleset rule, so a test can assert
    /// that the instance the event's arguments carry is the very one the rule threw — which
    /// <see cref="ThrowingValidator"/>, constructing a fresh exception per invocation, cannot
    /// support. The Submit ruleset exists and is empty, which is what lets a narrowed live
    /// profile select it and run nothing.
    /// </summary>
    private sealed class CapturedThrowValidator : DraftSubmitValidator<EngineOrder>
    {
        public Exception Boom { get; } = new InvalidOperationException("captured for identity");

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(_ => Boom is null ? true : throw Boom);

        protected override void ConfigureSubmitRules()
        {
        }
    }
}
