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
        RuleForEach(x => x.Items).ChildRules(item => item.RuleFor(x => x.Sku).NotEmpty());
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
