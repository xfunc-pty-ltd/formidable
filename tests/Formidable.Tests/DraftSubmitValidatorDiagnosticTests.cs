using System.Diagnostics;
using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

// The pin added below attaches its own TraceListener, which is process-global state - see
// ProcessGlobalStateCollection for how far it reaches and who owes membership.
[Collection(ProcessGlobalStateCollection.Name)]
public class DraftSubmitValidatorDiagnosticTests
{
    // Records the hook's calls. Its field initializer runs before the base constructor, so the
    // list exists by the time the scan calls the hook.
    private abstract class RecordingValidator : DraftSubmitValidator<TestOrder>
    {
        public readonly List<(string Property, string Validator)> Reported = [];

        protected override void OnOverlappingRuleAxes(string propertyName, string validatorName) =>
            Reported.Add((propertyName, validatorName));
    }

    private sealed class OverlappingValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).NotEmpty(); // presence rule on the DRAFT axis (wrong)

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty(); // and again on submit
    }

    private sealed class CleanValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).MaximumLength(10);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty();
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
    // the shipped fixture (display names, error codes, severities and collection child rules) is
    // what puts a realistic shape through its enumeration. Whether the scan REPORTS is pinned by
    // the two tests above, which can observe it because they override the hook; the pin below
    // reads the default hook's own message instead, through the listeners channel both
    // Debug.WriteLine and Trace.WriteLine write to.
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
    // hook itself, by attaching a listener for the duration of the constructor call. What it
    // proves is that the default hook reports through the listeners channel with the overlap
    // sentence intact; it cannot tell Trace.WriteLine apart from Debug.WriteLine, because a
    // Debug test build routes both to the same collection (Debug.Listeners is Trace.Listeners).
    // Debug.WriteLine's call site carries [Conditional("DEBUG")] and compiles away entirely in a
    // Release build; that Trace.WriteLine's call site survives Release is measured on the built
    // assembly, not asserted by this test.
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

    private sealed class CollectionRulesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleForEach(x => x.LineItems).ChildRules(item =>
                item.RuleFor(x => x.Quantity).LessThanOrEqualTo(100));

        protected override void ConfigureSubmitRules() =>
            RuleForEach(x => x.LineItems).ChildRules(item =>
                item.RuleFor(x => x.Sku).NotEmpty());
    }

    [Fact]
    public void Collection_child_rules_on_both_axes_are_not_reported()
    {
        var validator = new CollectionRulesValidator();

        Assert.Empty(validator.Reported);
    }

    private sealed class DifferentMustMessagesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40).WithMessage("Keep the description to 40 characters.");

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => !d.Contains("TBD")).WithMessage("Replace TBD before submitting.");
    }

    // Mutation: key a predicate by its type alone, as every other component is keyed. The two
    // Must rules then share a key and the pair is reported.
    [Fact]
    public void Two_Must_rules_with_different_messages_are_not_reported()
    {
        var validator = new DifferentMustMessagesValidator();

        Assert.Empty(validator.Reported);
    }

    private sealed class SameMustMessageValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40).WithMessage("Check the description.");

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => d.Trim().Length > 0).WithMessage("Check the description.");
    }

    // Mutation: leave predicate components out of the scan altogether. Nothing is reported, and
    // the one message a reader would see twice goes unflagged.
    [Fact]
    public void The_same_Must_message_on_both_axes_is_reported_once()
    {
        var validator = new SameMustMessageValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal(nameof(TestOrder.Description), report.Property);
        Assert.Equal("PredicateValidator`2", report.Validator);
    }

    private sealed class TwoPredicatePairsValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length <= 40).WithMessage("Keep the description short.")
                .Must(d => !d.Contains("TBD")).WithMessage("Replace TBD.");

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length <= 80).WithMessage("Keep the description short.")
                .Must(d => !d.Contains("tbd")).WithMessage("Replace TBD.");
    }

    // Mutation: call the hook once per matching (property, validator, message) key instead of
    // once per (property, validator) pair. Both messages repeat, so the hook fires twice with
    // the same arguments.
    [Fact]
    public void Two_predicate_pairs_on_one_property_report_the_pair_once()
    {
        var validator = new TwoPredicatePairsValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class MustAsyncValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(x => x.Description).MustAsync((d, _) => Task.FromResult(d.Length <= 40))
                .WithMessage("Keep the description to 40 characters.");
            RuleFor(x => x.Customer).MustAsync((c, _) => Task.FromResult(c is null || c.Name.Length <= 40))
                .WithMessage("Check the customer.");
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).MustAsync((d, _) => Task.FromResult(!d.Contains("TBD")))
                .WithMessage("Replace TBD before submitting.");
            RuleFor(x => x.Customer).MustAsync((c, _) => Task.FromResult(c is not null))
                .WithMessage("Check the customer.");
        }
    }

    // Mutation: treat only PredicateValidator<,> as a predicate, so MustAsync keys by type alone.
    // Description's two different messages then share a key and Description is reported too.
    [Fact]
    public void MustAsync_is_keyed_like_Must()
    {
        var validator = new MustAsyncValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Customer), "AsyncPredicateValidator`2"), report);
    }

    private sealed class NotEmptyWithMessagesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).NotEmpty().WithMessage("Describe the order.");

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty().WithMessage("A description is required to submit.");
    }

    // Mutation: key every component on its message, not only predicates. The two NotEmpty rules
    // carry different messages, so the pair goes unreported although one blank field shows both.
    [Fact]
    public void NotEmpty_on_both_axes_is_still_reported()
    {
        var validator = new NotEmptyWithMessagesValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal(nameof(TestOrder.Description), report.Property);
        Assert.Equal("NotEmptyValidator`2", report.Validator);
    }

    private sealed class DefaultMustMessagesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => !d.Contains("TBD"));
    }

    // The key is the unformatted message, so two unrelated predicates that both keep
    // FluentValidation's default message share it and are reported. A reader of that form sees
    // the same sentence under the field twice.
    // Mutation: skip a predicate whose message is its validator's default template. The pair
    // then goes unreported.
    [Fact]
    public void Two_Must_rules_with_the_default_message_are_reported()
    {
        var validator = new DefaultMustMessagesValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class InstanceReadingMessageValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40)
                .WithMessage(x => x.Description.ToUpperInvariant());

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => !d.Contains("TBD"))
                .WithMessage(x => x.Description.ToUpperInvariant());
    }

    // Reading a predicate's message outside validation hands its message lambda no instance, so
    // a lambda that reads the instance throws. The scan must neither let that escape the
    // constructor nor drop the pair: a component whose message cannot be read is compared by its
    // type alone.
    // Mutation: remove the guard around the message read. Construction then throws
    // NullReferenceException.
    [Fact]
    public void A_Must_whose_message_lambda_reads_the_instance_constructs_and_reports_the_pair()
    {
        InstanceReadingMessageValidator? validator = null;

        var exception = Record.Exception(() => validator = new InstanceReadingMessageValidator());

        Assert.Null(exception);
        var report = Assert.Single(validator!.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class OneUnreadableMessageValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40)
                .WithMessage(x => x.Description.ToUpperInvariant());

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => !d.Contains("TBD")).WithMessage("Replace TBD.");
    }

    // A message that cannot be read says nothing about whether the two messages differ, so its
    // component is compared by type alone and matches any Must on the other axis.
    // Mutation: let an unreadable message match only another unreadable message. The pair then
    // goes unreported.
    [Fact]
    public void A_Must_whose_message_cannot_be_read_is_compared_by_type_alone()
    {
        var validator = new OneUnreadableMessageValidator();

        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class MessageLambdaOnOneAxisValidator : RecordingValidator
    {
        public int MessageReads;

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40).WithMessage(_ =>
            {
                MessageReads++;
                return "Keep the description to 40 characters.";
            });

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty();
    }

    // Reading a message runs a consumer's message lambda at construction, with no instance. The
    // scan reads a message only when the other axis holds a rule of the same type on the same
    // property, so a Must with no counterpart runs no lambda.
    // Mutation: read messages while building the two sets in phase one. The lambda then runs
    // once and MessageReads is 1.
    [Fact]
    public void A_message_lambda_does_not_run_when_the_Must_has_no_counterpart()
    {
        var validator = new MessageLambdaOnOneAxisValidator();

        Assert.Equal(0, validator.MessageReads);
        Assert.Empty(validator.Reported);
    }

    // Stands in for a resource class, the localisation idiom: each message is a static property
    // that a WithMessage lambda returns without reading the model.
    private static class Messages
    {
        public static string A => "Keep the description to 40 characters.";

        public static string B => "Replace TBD before submitting.";
    }

    private sealed class LocalisedMessagesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40).WithMessage(_ => Messages.A);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).Must(d => !d.Contains("TBD")).WithMessage(_ => Messages.B);
    }

    // A pin, not a red-first test: a lambda that ignores the model reads at construction, so two
    // localised messages that differ are told apart as two constants are.
    // Mutation: count a message set by a lambda as matching without reading it. The pair is then
    // reported.
    [Fact]
    public void Localised_message_factories_that_differ_are_not_reported()
    {
        var validator = new LocalisedMessagesValidator();

        Assert.Empty(validator.Reported);
    }

    // Counts the message lambdas the scan runs. Its field initializer runs before the base
    // constructor, as the recorded list's does.
    private abstract class MessageCountingValidator : RecordingValidator
    {
        public int MessageCalls;

        // Counts the call, then reads the instance, which is null at construction.
        protected string CountThenRead(TestOrder order)
        {
            MessageCalls++;
            return order.Description;
        }

        // Counts the call and returns the message without reading the model.
        protected string CountThenReturn(string message)
        {
            MessageCalls++;
            return message;
        }
    }

    private sealed class ThreeInstanceReadingMustsValidator : MessageCountingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length <= 40).WithMessage(x => CountThenRead(x))
                .Must(d => !d.Contains("TBD")).WithMessage(x => CountThenRead(x))
                .Must(d => d.Trim() == d).WithMessage(x => CountThenRead(x));

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length > 0).WithMessage(x => CountThenRead(x))
                .Must(d => !d.Contains("tbd")).WithMessage(x => CountThenRead(x))
                .Must(d => d.Length <= 80).WithMessage(x => CountThenRead(x));
    }

    // The first message that cannot be read already answers "matching" for its key, so no other
    // message on that key is read.
    // Mutation: read every message on both axes before testing. All six lambdas then run.
    [Fact]
    public void Instance_reading_messages_run_one_lambda_per_shared_key()
    {
        var validator = new ThreeInstanceReadingMustsValidator();

        Assert.Equal(1, validator.MessageCalls);
        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class UnreadableSubmitMessagesValidator : MessageCountingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40)
                .WithMessage(_ => CountThenReturn("Keep the description short."));

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length > 0).WithMessage(x => CountThenRead(x))
                .Must(d => !d.Contains("tbd")).WithMessage(x => CountThenRead(x))
                .Must(d => d.Length <= 80).WithMessage(x => CountThenRead(x));
    }

    // The draft side reads its one message. The submit side stops at its first message that
    // cannot be read, which already answers "matching", so two lambdas run in all.
    // Mutation: read every submit message before testing. All three submit lambdas then run, for
    // four calls.
    [Fact]
    public void The_submit_side_stops_at_its_first_unreadable_message()
    {
        var validator = new UnreadableSubmitMessagesValidator();

        Assert.Equal(2, validator.MessageCalls);
        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class SharedSubmitMessageFirstValidator : MessageCountingValidator
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).Must(d => d.Length <= 40)
                .WithMessage(_ => CountThenReturn("Check the description."));

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description)
                .Must(d => d.Length > 0).WithMessage(_ => CountThenReturn("Check the description."))
                .Must(d => !d.Contains("tbd")).WithMessage(x => CountThenRead(x))
                .Must(d => d.Length <= 80).WithMessage(x => CountThenRead(x));
    }

    // The submit side's first message repeats the draft side's, which already answers
    // "matching", so no submit message after it is read and two lambdas run in all.
    // Mutation: keep reading the submit side after a message the draft side holds, stopping only
    // at one that cannot be read. The next submit lambda then runs too, for three calls.
    [Fact]
    public void The_submit_side_stops_at_its_first_message_the_draft_side_holds()
    {
        var validator = new SharedSubmitMessageFirstValidator();

        Assert.Equal(2, validator.MessageCalls);
        var report = Assert.Single(validator.Reported);
        Assert.Equal((nameof(TestOrder.Description), "PredicateValidator`2"), report);
    }

    private sealed class CustomRulesValidator : RecordingValidator
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(x => x.Description).Custom((d, context) =>
            {
                if (d.Length > 40)
                {
                    context.AddFailure("Keep the description to 40 characters.");
                }
            });
            RuleFor(x => x.Customer).CustomAsync((c, context, _) => Task.CompletedTask);
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).Custom((d, context) =>
            {
                if (d.Contains("TBD"))
                {
                    context.AddFailure("Replace TBD before submitting.");
                }
            });
            RuleFor(x => x.Customer).CustomAsync((c, context, _) => Task.CompletedTask);
        }
    }

    // A pin, not a red-first test, of FluentValidation's shape: Custom is a Must that always
    // passes and CustomAsync a MustAsync, each keeping the default message, and WithMessage
    // cannot follow either. Two of a kind on one property share a key, whatever failures their
    // bodies add, so they are reported.
    // Mutation: skip a predicate whose message is its validator's default template. Neither pair
    // is then reported.
    [Fact]
    public void Two_Custom_or_two_CustomAsync_rules_on_one_property_are_reported()
    {
        var validator = new CustomRulesValidator();

        Assert.Equal(2, validator.Reported.Count);
        Assert.Contains((nameof(TestOrder.Description), "PredicateValidator`2"), validator.Reported);
        Assert.Contains((nameof(TestOrder.Customer), "AsyncPredicateValidator`2"), validator.Reported);
    }
}
