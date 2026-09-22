using FluentValidation;
using Formidable;
using Formidable.Blazor;

namespace Formidable.Blazor.Tests.Fixtures;

public sealed class EngineOrder
{
    public string Description { get; set; } = string.Empty;
    public EngineCustomer? Customer { get; set; }
    public List<EngineItem> Items { get; set; } = [];
    public EnginePoint Location { get; set; }
}

public sealed class EngineCustomer
{
    public string Name { get; set; } = string.Empty;
}

public sealed class EngineItem
{
    public string Sku { get; set; } = string.Empty;
}

public struct EnginePoint
{
    public int X { get; set; }
}

public sealed class EngineOrderValidator : DraftSubmitValidator<EngineOrder>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(x => x.Description).MaximumLength(10);
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(x => x.Description).NotEmpty().WithName("Order description");
        RuleFor(x => x.Customer).NotNull();
        RuleForEach(x => x.Items).ChildRules(item => item.RuleFor(x => x.Sku).NotEmpty().WithMessage("SKU is required"));
        RuleFor(x => x).Must(o => o.Items.Count <= 3).WithMessage("No more than 3 items");
        RuleFor(x => x.Description)
            .Must(d => !d.Contains('-'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Avoid hyphens");
    }
}

/// <summary>Submit validator whose async rule blocks on <see cref="Gate"/> until released, letting tests observe in-flight passes.</summary>
public sealed class GatedValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; private set; } = new();
    public int Started;
    public CancellationToken LastToken { get; private set; }

    /// <summary>
    /// The rule's outcome once <see cref="Gate"/> releases it — read only after the await, so a
    /// caller controlling several overlapping gates independently can flip this between releases
    /// to give each one a distinct, observable answer. Defaults to <see langword="false"/>,
    /// preserving every pre-existing test's "async says no" expectation unchanged.
    /// </summary>
    public bool ShouldPass { get; set; }

    /// <summary>
    /// Raised right after a gate releases the rule, before it returns — the only observable
    /// signal a caller has for "this particular gated invocation has reached its answer" when
    /// the caller that started it (a fire-and-forget probe, say) hands back no awaitable handle
    /// of its own.
    /// </summary>
    public event Action? Released;

    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).MustAsync(async (_, ct) =>
        {
            Started++;
            LastToken = ct;
            await Gate.Task.WaitAsync(ct);
            Released?.Invoke();
            return ShouldPass;
        }).WithMessage("async says no");

    public void Reset() => Gate = new TaskCompletionSource();
}

/// <summary>
/// Submit validator whose async rule blocks on <see cref="Gate"/> without observing the
/// cancellation token it is handed — unlike <see cref="GatedValidator"/>, cancelling that token
/// does nothing here: the rule only resolves once <see cref="Gate"/> is released, and it always
/// passes. Exists to exercise the one case a token-honoring validator (every other fixture in
/// this file) cannot: a stale pass that outruns Dispose instead of being cut short by it.
/// </summary>
public sealed class CancellationIgnoringValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; } = new();
    public int Started;

    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).MustAsync(async (_, _) =>
        {
            Started++;
            await Gate.Task;
            return true;
        });
}

/// <summary>Draft validator whose rule throws when <see cref="Throw"/> is true, for exercising fault-handling paths.</summary>
public sealed class ThrowingValidator : DraftSubmitValidator<EngineOrder>
{
    public bool Throw { get; set; } = true;

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Description).Must(_ => Throw ? throw new InvalidOperationException("rule blew up") : true);

    protected override void ConfigureSubmitRules()
    {
    }
}

/// <summary>
/// Draft validator whose <see cref="EngineOrder.Description"/> rule blocks on <see cref="Gate"/>
/// (mirrors the sample's async uniqueness check) while the customer-name and item-SKU rules fail
/// synchronously — so a live pass can be held in flight, across the refresh debounce if need be,
/// with ordinary field verdicts riding on whichever pass wins.
/// </summary>
public sealed class SlowLiveRuleValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; private set; } = new();

    protected override void ConfigureDraftRules()
    {
        RuleFor(x => x.Description).MustAsync(async (_, ct) =>
        {
            await Gate.Task.WaitAsync(ct);
            return true;
        });

        RuleFor(x => x.Customer!.Name)
            .MaximumLength(3).WithMessage("Customer name is too long")
            .When(x => x.Customer is not null);

        RuleForEach(x => x.Items).ChildRules(item =>
            item.RuleFor(i => i.Sku).MaximumLength(3).WithMessage("SKU is too long"));
    }

    protected override void ConfigureSubmitRules()
    {
    }

    public void Reset() => Gate = new TaskCompletionSource();
}

/// <summary>
/// Draft validator with two independent async rules (mirrors the sample's <c>HandleValidator</c>
/// shape): the <see cref="EngineOrder.Description"/> rule resolves on its own, while the
/// <see cref="EngineCustomer.Name"/> rule blocks on <see cref="CustomerNameGate"/> until
/// released — for asserting that a live pass triggered by editing one async field does not mark
/// a sibling async field as validating too.
/// </summary>
public sealed class TwoAsyncFieldsValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource CustomerNameGate { get; private set; } = new();

    protected override void ConfigureDraftRules()
    {
        RuleFor(x => x.Description).MustAsync(async (_, _) =>
        {
            await Task.Yield();
            return true;
        });

        RuleFor(x => x.Customer!.Name)
            .MustAsync(async (_, ct) =>
            {
                await CustomerNameGate.Task.WaitAsync(ct);
                return true;
            })
            .When(x => x.Customer is not null);
    }

    protected override void ConfigureSubmitRules()
    {
    }

    public void Reset() => CustomerNameGate = new TaskCompletionSource();
}

/// <summary>
/// Wraps a real validator and counts calls, split out by profile — <see cref="SubmitProfileCallCount"/>
/// is the signature a <see cref="FormidableOptions.TrackFormValidity"/> probe leaves behind (it
/// always validates <paramref name="submitProfile"/>, the caller's own configured
/// <see cref="FormidableOptions.SubmitProfile"/> rather than the <see cref="ValidationProfile.Submit"/>
/// static — a test that overrides the option would otherwise silently stop being pinned), distinct
/// from an ordinary live pass validating under <see cref="FormidableOptions.LiveProfile"/> — which
/// is what lets a test prove the probe never ran without also having to silence the live pass it
/// rides alongside.
/// </summary>
public sealed class CountingValidator(
    IModelValidator<EngineOrder> inner, ValidationProfile submitProfile) : IModelValidator<EngineOrder>
{
    public int CallCount { get; private set; }
    public int SubmitProfileCallCount { get; private set; }

    public Task<ValidationReport> ValidateAsync(
        EngineOrder model, ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        Count(profile);
        return inner.ValidateAsync(model, profile, cancellationToken);
    }

    public ValidationReport Validate(EngineOrder model, ValidationProfile profile)
    {
        Count(profile);
        return inner.Validate(model, profile);
    }

    private void Count(ValidationProfile profile)
    {
        CallCount++;
        if (profile.Equals(submitProfile))
        {
            SubmitProfileCallCount++;
        }
    }
}

/// <summary>
/// Normalizable model pinning <see cref="FormidableOptions.NormalizeOnSubmit"/>: its
/// <see cref="Normalize"/> trims <see cref="Description"/>, and the paired validator's rule
/// fails on the untrimmed value but passes on the trimmed one, so a submit's outcome tells the
/// test whether normalization ran before the profile did.
/// </summary>
public sealed class NormalizableOrder : INormalizableModel
{
    public string Description { get; set; } = string.Empty;

    public void Normalize() => Description = Description.Trim();
}

public sealed class NormalizableOrderValidator : DraftSubmitValidator<NormalizableOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).MaximumLength(2);
}

/// <summary>Synchronization helpers shared by the engine's async-pass tests.</summary>
public static class EngineTestSync
{
    /// <summary>
    /// Completes the next time the engine reports it is no longer validating. Call it only once
    /// the pass under test is confirmed in flight — StateChanged also fires before a pass flips
    /// IsValidating true (MarkTouched does), which would resolve quiescence prematurely.
    /// </summary>
    public static Task Quiescence(FormValidationEngine<EngineOrder> engine)
    {
        var quiescent = new TaskCompletionSource();
        engine.StateChanged += () =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };
        return quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
