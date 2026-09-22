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
        RuleFor(x => x.Description)
            .Must(d => !d.Contains('!'))
            .WithSeverity(Severity.Info)
            .WithMessage("Exclamation marks read as shouting");
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
/// Submit validator whose Submit ruleset fails <see cref="EngineOrder.Description"/> with an
/// ordinary field error, and whose Draft ruleset throws when <see cref="Throw"/> is true —
/// unlike <see cref="ThrowingValidator"/>, which never fails a submit at all. Exists to stage a
/// live-pass fault landing ALONGSIDE a field error a prior submit already made visible, since
/// the fault issue does not clear or replace what a submit put in the visible set.
/// </summary>
public sealed class FaultAndFieldErrorValidator : DraftSubmitValidator<EngineOrder>
{
    public bool Throw { get; set; }

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Description).Must(_ => Throw ? throw new InvalidOperationException("rule blew up") : true);

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required");
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
/// <remarks>
/// The submit ruleset carries a model-level rule on the same gate, which never fails and so
/// changes no verdict. A post-submit refresh runs only what the live pass left out, so holding a
/// refresh in flight means holding a rule from the submit bucket; a gate on the draft bucket alone
/// holds live passes only. It carries the same customer guard the gated draft rule does, so a
/// model this fixture's draft rules would skip cannot block on the gate with nothing to release it.
/// </remarks>
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

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x)
            .MustAsync(async (_, ct) =>
            {
                await CustomerNameGate.Task.WaitAsync(ct);
                return true;
            })
            .When(x => x.Customer is not null);

    public void Reset() => CustomerNameGate = new TaskCompletionSource();
}

/// <summary>
/// Submit validator whose rules are declared in an order no page would render them in: a
/// form-level rule first, then two <see cref="EngineOrder.Description"/> rules, then the
/// customer's name last. Every rule fails on a default order, so a test can hand the engine any
/// field order it likes and still tell the two apart — and the two description messages are what
/// pin that ordering by field keeps one field's issues in the order the validator produced them.
/// </summary>
public sealed class DeclarationOrderValidator : DraftSubmitValidator<EngineOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(x => x).Must(_ => false).WithMessage("The order is incomplete");
        RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required");
        RuleFor(x => x.Description).MinimumLength(5).WithMessage("Description is too short");
        RuleFor(x => x.Customer!.Name)
            .NotEmpty().WithMessage("Customer name is required")
            .When(x => x.Customer is not null);
    }
}

/// <summary>
/// Submit validator that fails <see cref="EngineOrder.Description"/> with a WARNING and
/// <see cref="EngineCustomer.Name"/> with an error, for a page that renders the advisory-bearing
/// field above the erroring one: the topmost visible issue is then an advisory, while the thing a
/// blocked submit has to take the visitor to is still the error below it.
/// </summary>
public sealed class AdvisoryAboveErrorValidator : DraftSubmitValidator<EngineOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(x => x.Description)
            .Must(_ => false)
            .WithSeverity(Severity.Warning)
            .WithMessage("Description could be clearer");
        RuleFor(x => x.Customer!.Name)
            .NotEmpty().WithMessage("Customer name is required")
            .When(x => x.Customer is not null);
    }
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

/// <summary>
/// Validator whose two profiles fail different fields, so a test can tell the live channel and
/// the submit channel apart by message alone. The draft rule fails <see cref="EngineCustomer.Name"/>
/// — a field no submit here ever makes an error site, so a refresh filters its own copy of that
/// verdict straight back out and only a live pass can put it on the field. The submit rule fails
/// <see cref="EngineOrder.Description"/> for as long as the customer has no name, so a single edit
/// to the name breaks the draft rule and clears the submit rule at once: each channel then has an
/// observable transition of its own, and neither one's verdict can be mistaken for the other's.
/// </summary>
public sealed class ChannelSeparatingValidator : DraftSubmitValidator<EngineOrder>
{
    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Customer!.Name)
            .MaximumLength(4).WithMessage("Customer name must be four characters or fewer")
            .When(x => x.Customer is not null);

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description)
            .Must((order, _) => order.Customer is { Name.Length: > 0 })
            .WithMessage("Description needs a named customer");
}

/// <summary>
/// <see cref="ChannelSeparatingValidator"/> with both of its rules made asynchronous and blocked
/// on <see cref="Gate"/> — which is what lets a test hold either a live pass or a refresh pass in
/// flight while the other's debounce window comes due. Both buckets are gated because the two
/// passes run different ones: a live pass runs the draft bucket, and a post-submit refresh runs
/// only what that pass left out, which is the submit bucket.
/// </summary>
public sealed class GatedChannelSeparatingValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; private set; } = new();

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Customer!.Name)
            .MustAsync(async (name, ct) =>
            {
                await Gate.Task.WaitAsync(ct);
                return name.Length <= 4;
            })
            .WithMessage("Customer name must be four characters or fewer")
            .When(x => x.Customer is not null);

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description)
            .MustAsync(async (order, _, ct) =>
            {
                await Gate.Task.WaitAsync(ct);
                return order.Customer is { Name.Length: > 0 };
            })
            .WithMessage("Description needs a named customer");

    public void Reset() => Gate = new TaskCompletionSource();
}

/// <summary>
/// Counts how often each rule bucket executes. Running a rule twice leaves exactly the issues
/// running it once leaves, so an execution counter is the only thing that can tell the two apart.
/// The draft bucket fails <see cref="EngineCustomer.Name"/> beyond four characters and the submit
/// bucket requires <see cref="EngineOrder.Description"/>, so each bucket also carries a message
/// naming which one produced it. <see cref="ExtraRuleSetName"/> is registered with no rules of its
/// own: a live profile naming it selects exactly what the draft bucket alone selects while still
/// naming something the submit profile does not, which is what makes such a pair unsubtractable
/// without changing a single verdict.
/// </summary>
public sealed class RuleRunCountingValidator : DraftSubmitValidator<EngineOrder>
{
    public const string DraftMessage = "Customer name must be four characters or fewer";
    public const string SubmitMessage = "Description is required";
    public const string ExtraRuleSetName = "Extra";

    public int DraftRuleRuns;
    public int SubmitRuleRuns;

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Customer!.Name)
            .Must(name =>
            {
                DraftRuleRuns++;
                return name.Length <= 4;
            })
            .WithMessage(DraftMessage)
            .When(x => x.Customer is not null);

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description)
            .Must(description =>
            {
                SubmitRuleRuns++;
                return description.Length > 0;
            })
            .WithMessage(SubmitMessage);

    protected override void ConfigureAdditionalProfiles() => Profile(ExtraRuleSetName, () => { });
}

/// <summary>
/// <see cref="RuleRunCountingValidator"/> with its draft rule made asynchronous and blocked on
/// <see cref="Gate"/>, so a live pass can be held in flight while an edit lands — the one window
/// in which the edit stamp a live pass takes as it begins differs from the one standing when its
/// verdict arrives. The counter is incremented before the rule blocks, so it counts executions
/// that were reached rather than executions that got an answer.
/// </summary>
public sealed class GatedRuleRunCountingValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; private set; } = new();

    public int DraftRuleRuns;
    public int SubmitRuleRuns;

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Customer!.Name)
            .MustAsync(async (name, ct) =>
            {
                DraftRuleRuns++;
                await Gate.Task.WaitAsync(ct);
                return name.Length <= 4;
            })
            .WithMessage(RuleRunCountingValidator.DraftMessage)
            .When(x => x.Customer is not null);

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description)
            .Must(description =>
            {
                SubmitRuleRuns++;
                return description.Length > 0;
            })
            .WithMessage(RuleRunCountingValidator.SubmitMessage);

    public void Reset() => Gate = new TaskCompletionSource();
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
