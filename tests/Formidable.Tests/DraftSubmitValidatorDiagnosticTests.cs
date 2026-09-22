using System.Diagnostics;
using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

// The pin added below attaches its own TraceListener, which is process-global state - see
// ProcessGlobalStateCollection for how far it reaches and who owes membership.
[Collection(ProcessGlobalStateCollection.Name)]
public class DraftSubmitValidatorDiagnosticTests
{
    private sealed class OverlappingValidator : DraftSubmitValidator<TestOrder>
    {
        public readonly List<(string Property, string Validator)> Reported = [];

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).NotEmpty(); // presence rule on the DRAFT axis (wrong)

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty(); // and again on submit

        protected override void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
            Reported.Add((propertyName, validatorName));
    }

    private sealed class CleanValidator : DraftSubmitValidator<TestOrder>
    {
        public readonly List<(string Property, string Validator)> Reported = [];

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).MaximumLength(10);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty();

        protected override void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
            Reported.Add((propertyName, validatorName));
    }

    [Fact]
    public void Overlapping_same_validator_on_both_axes_is_reported()
    {
        var validator = new OverlappingValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal(nameof(TestOrder.Description), report.Property);
        Assert.Contains("NotEmpty", report.Validator);
    }

    [Fact]
    public void Different_validators_on_each_axis_are_not_reported()
    {
        var validator = new CleanValidator();

        Assert.Empty(validator.Reported);
    }

    // The overlap scan runs from the base constructor over every rule the validator declares, so
    // the shipped fixture — display names, error codes, severities and collection child rules —
    // is what puts a realistic shape through its enumeration. Whether the scan REPORTS is pinned
    // by the two tests above, which can observe it because they override the hook; the default
    // hook writes to Trace, which a release build of this library keeps, so the pin below reads
    // that channel directly instead of through an override.
    [Fact]
    public void Existing_fixture_validator_constructs_through_the_overlap_scan()
    {
        var exception = Record.Exception(() => new TestOrderValidator());

        Assert.Null(exception);
    }

    private sealed class UnoverriddenOverlapValidator : DraftSubmitValidator<TestOrder>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).NotEmpty();

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty();
    }

    // The two tests above observe the scan through an override; this one observes the default
    // hook itself, which is the shape every consumer who never overrides it gets. A release build
    // of this library keeps the Trace call site, so a listener attached for the duration of the
    // constructor call is enough to read it back.
    [Fact]
    public void Default_hook_writes_the_overlap_message_to_Trace()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            _ = new UnoverriddenOverlapValidator();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains(listener.Lines, line => line.Contains("has") && line.Contains("rules in both the draft and submit axes"));
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Lines { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => Lines.Add(message ?? string.Empty);
    }

    private sealed class CollectionRulesValidator : DraftSubmitValidator<TestOrder>
    {
        public readonly List<(string Property, string Validator)> Reported = [];

        protected override void ConfigureDraftRules() =>
            RuleForEach(x => x.LineItems).ChildRules(item =>
                item.RuleFor(x => x.Quantity).LessThanOrEqualTo(100));

        protected override void ConfigureSubmitRules() =>
            RuleForEach(x => x.LineItems).ChildRules(item =>
                item.RuleFor(x => x.Sku).NotEmpty());

        protected override void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
            Reported.Add((propertyName, validatorName));
    }

    [Fact]
    public void Collection_child_rules_on_both_axes_are_not_reported()
    {
        var validator = new CollectionRulesValidator();

        Assert.Empty(validator.Reported);
    }
}
