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
/// does nothing here: the rule only resolves once <see cref="Gate"/> is released. Exists to
/// exercise the one case a token-honoring validator (every other fixture in this file) cannot: a
/// stale pass that outruns Dispose instead of being cut short by it.
/// </summary>
public sealed class CancellationIgnoringValidator : DraftSubmitValidator<EngineOrder>
{
    public TaskCompletionSource Gate { get; } = new();
    public int Started;

    /// <summary>
    /// The rule's outcome once <see cref="Gate"/> releases it. Defaults to <see langword="true"/>
    /// — the opposite of <see cref="GatedValidator.ShouldPass"/>, because the two fixtures are
    /// reached for different reasons: a caller here wants a stale pass that outran disposal to
    /// carry a verdict a dead-engine guard has to suppress, and a PASSING one is what proves the
    /// guard rather than supersession is doing the suppressing. Set it false when the verdict
    /// itself has to be observable, e.g. as the errors a suppressed outcome must not report.
    /// </summary>
    public bool ShouldPass { get; set; } = true;

    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Description).MustAsync(async (_, _) =>
        {
            Started++;
            await Gate.Task;
            return ShouldPass;
        }).WithMessage("the abandoned pass says no");
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
/// refresh in flight means holding a rule from the submit bucket AND narrowing the live channel
/// to the draft bucket, which is what leaves that rule for the refresh to reach; a gate on the
/// draft bucket alone holds live passes only. It carries the same customer guard the gated draft
/// rule does, so a model this fixture's draft rules would skip cannot block on the gate with
/// nothing to release it.
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
/// Wraps a real validator and counts whole-profile validations. Counting them all, rather than
/// splitting the count by profile, is what keeps the counter discriminating: the live channel
/// selects the submit profile's own rules unless a test narrows it, so the profile a call carries
/// does not say which caller made it. What each count means is a matter of arithmetic at the call
/// site — a pristine engine and one field change cost one validation with tracking off and three
/// with it on.
/// </summary>
public sealed class CountingValidator<TModel>(IModelValidator<TModel> inner) : IModelValidator<TModel>
{
    public int CallCount { get; private set; }

    public Task<ValidationReport> ValidateAsync(
        TModel model, ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return inner.ValidateAsync(model, profile, cancellationToken);
    }

    public ValidationReport Validate(TModel model, ValidationProfile profile)
    {
        CallCount++;
        return inner.Validate(model, profile);
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
/// Validator whose two rule buckets fail different fields, so a test that narrows the live
/// channel to the draft bucket can tell the live channel and the submit channel apart by message
/// alone. The draft rule fails <see cref="EngineCustomer.Name"/>
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
/// passes run different ones once the live channel is narrowed to the draft bucket: the live pass
/// runs that bucket, and a post-submit refresh runs only what it left out, which is the submit
/// bucket.
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
/// naming which one produced it.
/// </summary>
public sealed class RuleRunCountingValidator : DraftSubmitValidator<EngineOrder>
{
    public const string DraftMessage = "Customer name must be four characters or fewer";
    public const string SubmitMessage = "Description is required";

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

/// <summary>
/// Hides an inner validator's rule-level capability behind the bare
/// <see cref="IModelValidator{TModel}"/> surface — the wrapper implements nothing else, so an
/// engine's capability test fails and every pass takes the whole-profile fallback. Exists to pin
/// that the fallback stays verdict-correct while paying full-profile execution, and that no
/// store or scheduling machinery is consulted on its behalf.
/// </summary>
public sealed class CapabilityHidingModelValidator<TModel>(IModelValidator<TModel> inner)
    : IModelValidator<TModel>
{
    public Task<ValidationReport> ValidateAsync(
        TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        inner.ValidateAsync(model, profile, cancellationToken);

    public ValidationReport Validate(TModel model, ValidationProfile profile) =>
        inner.Validate(model, profile);
}

/// <summary>
/// A form whose values arrive from somewhere other than the visitor's typing, shaped so one
/// saved state produces all three draft-load outcomes at once: <see cref="Title"/> holds a good
/// saved value, <see cref="Summary"/> was never filled in, and <see cref="ContactEmail"/> holds
/// a value that is present but wrong.
/// </summary>
public sealed class LoadedDraft
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
}

/// <summary>
/// Rules chosen so that each of <see cref="LoadedDraft"/>'s fields fails in a different way.
/// <see cref="LoadedDraft.ContactEmail"/> carries a presence rule AND a format rule, so a
/// non-empty malformed address is a field that holds a value and still fails — the case that
/// separates "you have not got to this yet" from "this one is wrong".
/// <see cref="LoadedDraft.Summary"/> carries a presence rule AND an unconditional INFO advisory,
/// so an unfilled summary has something to say and nobody to say it about — the shape that
/// tells a classification reading the VALUE from one reading the issues.
/// </summary>
public sealed class LoadedDraftValidator : DraftSubmitValidator<LoadedDraft>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(d => d.ContactEmail)
            .Must(email => email.Contains('@', StringComparison.Ordinal))
            .WithMessage("That is not a valid email address")
            .When(d => d.ContactEmail.Length > 0);

        RuleFor(d => d.Summary)
            .Must(_ => false)
            .WithSeverity(Severity.Info)
            .WithMessage("A summary helps reviewers find this later");
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(d => d.Title).NotEmpty().WithMessage("Title is required");
        RuleFor(d => d.Summary).NotEmpty().WithMessage("Summary is required");
        RuleFor(d => d.ContactEmail).NotEmpty().WithMessage("A contact email is required");
    }
}

/// <summary>
/// <see cref="LoadedDraft.Title"/>'s presence rule and its length rule are given the SAME
/// <c>WithErrorCode</c>, so the code a failure carries identifies neither of them — the shape
/// that shows a draft load deciding on the value rather than on what the failure names.
/// </summary>
public sealed class AmbiguousCodeDraftValidator : DraftSubmitValidator<LoadedDraft>
{
    public const string SharedCode = "TITLE";

    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        // Declared first, and failing on the same value, so it is the failure a short-circuiting
        // ask would stop at - which is what makes the ambiguity below reportable-or-not rather
        // than reported by construction. Its own code identifies one kind of rule, so it is
        // ordinary in every other respect.
        RuleFor(d => d.Title).MaximumLength(3).WithMessage("Title is over three characters");

        RuleFor(d => d.Title).NotEmpty().WithErrorCode(SharedCode).WithMessage("Title is required");
        RuleFor(d => d.Title)
            .Must(title => title.Length <= 3)
            .WithErrorCode(SharedCode)
            .WithMessage("Title is too long");
    }
}

/// <summary>
/// Presence written the one way inspection cannot see: a predicate. FluentValidation's presence
/// marker interfaces are what requirement detection reads, so a
/// <c>Must(s =&gt; !string.IsNullOrWhiteSpace(s))</c> is indistinguishable from a range check
/// however plainly it reads as a presence check, and
/// <see cref="FormidableOptions.RequiredOverride"/> is what a form marking such a field uses.
/// A draft load never has to tell the two apart: it reads the value, which an empty box answers
/// the same way whichever rule is objecting to it.
/// </summary>
public sealed class PredicatePresenceDraftValidator : DraftSubmitValidator<LoadedDraft>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(d => d.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title))
            .WithMessage("Title is required");
}

/// <summary>
/// A field carrying a presence component AND a predicate. Under the default Continue cascade an
/// empty value fails BOTH, so the field's failures carry a presence code and a non-presence code
/// at once - the shape that shows what a per-failure classification does when a field's failures
/// disagree about what is wrong with it.
/// </summary>
public sealed class PresenceAlongsidePredicateValidator : DraftSubmitValidator<LoadedDraft>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(d => d.Title)
            .NotEmpty()
            .Must(title => !string.IsNullOrWhiteSpace(title))
            .WithMessage("Title is required");
}

/// <summary>
/// The same chain under rule-level <see cref="CascadeMode.Stop"/>, where the presence
/// component's failure is the only one an empty value produces - the same two components, one
/// failure. Rule-level cascade is deliberately not class-level: inspection reads the descriptor
/// either way, and per-rule EXECUTION is only refused for a class-level Stop.
/// </summary>
public sealed class StoppingPresenceValidator : DraftSubmitValidator<LoadedDraft>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(d => d.Title)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(title => !string.IsNullOrWhiteSpace(title))
            .WithMessage("Title is required");
}

/// <summary>
/// A saved draft whose interesting fields live inside a collection. One row was filled in
/// correctly, one holds a value that is present and wrong, and one was never filled in — the
/// three draft-load outcomes, all of them on paths a failure carries an index for.
/// </summary>
public sealed class LoadedRoster
{
    public string Owner { get; set; } = string.Empty;

    public List<LoadedRow> Rows { get; set; } = [];
}

/// <summary>One row of <see cref="LoadedRoster"/>.</summary>
public sealed class LoadedRow
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Rules declared where only a walk through the child validator can see them: a presence rule
/// and a format rule on the same row field, so a blank row and a wrongly-filled row fail
/// different rules.
/// </summary>
public sealed class LoadedRosterValidator : DraftSubmitValidator<LoadedRoster>
{
    protected override void ConfigureDraftRules() =>
        RuleForEach(r => r.Rows).ChildRules(row =>
            row.RuleFor(x => x.Code)
                .Must(code => code.StartsWith("R-", StringComparison.Ordinal))
                .WithMessage("Row codes start with R-")
                .When(x => x.Code.Length > 0));

    protected override void ConfigureSubmitRules()
    {
        RuleFor(r => r.Owner).NotEmpty().WithMessage("Owner is required");
        RuleForEach(r => r.Rows).ChildRules(row =>
            row.RuleFor(x => x.Code).NotEmpty().WithMessage("Row code is required"));
    }
}

/// <summary>
/// A saved draft whose fields are typed rather than stringly: two non-nullable value types
/// holding their own defaults, and two nullable ones holding the underlying type's default.
/// FluentValidation's NotEmpty() reads the DECLARED type, so it fails the first pair and passes
/// the second - the discrimination a draft load has to reproduce to tell a saved answer from a
/// never-filled box.
/// </summary>
public sealed class TypedDraft
{
    public int Quantity { get; set; }

    public int? Adjustment { get; set; }

    public bool Accepted { get; set; }

    public bool? Subscribed { get; set; }
}

/// <summary>One presence rule per field of <see cref="TypedDraft"/>, and nothing else.</summary>
public sealed class TypedDraftValidator : DraftSubmitValidator<TypedDraft>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(d => d.Quantity).NotEmpty().WithMessage("Quantity is required");
        RuleFor(d => d.Adjustment).NotEmpty().WithMessage("Adjustment is required");
        RuleFor(d => d.Accepted).NotEmpty().WithMessage("Acceptance is required");
        RuleFor(d => d.Subscribed).NotEmpty().WithMessage("A subscription answer is required");
    }
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
