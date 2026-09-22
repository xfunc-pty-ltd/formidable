using FluentValidation;
using Formidable;

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

    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).MustAsync(async (_, ct) =>
        {
            Started++;
            LastToken = ct;
            await Gate.Task.WaitAsync(ct);
            return false;
        }).WithMessage("async says no");

    public void Reset() => Gate = new TaskCompletionSource();
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
