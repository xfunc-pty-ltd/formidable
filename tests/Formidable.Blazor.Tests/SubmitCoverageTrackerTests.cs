using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="SubmitCoverageTracker"/>'s contract at its own seam, with no engine anywhere:
/// the ask carries its own coordinates (edit stamp, submit profile, rule selection), so a test
/// plays the engine's sites by hand — filing store answers, noting edits, moving the version the
/// way a pass end or a field-set change does — and asserts what the vouch serves. The two serve
/// routes (a matching stamp; a moved stamp with a re-answer on its way), the per-read re-check
/// of a served answer, the edited-field exclusion, the profile's reference identity, the
/// capability-less record, and the throw-reads-as-stale contract each get one condition per
/// test. The engine-level pins in <see cref="FormidableEngineSubmitCoverageTests"/> exercise
/// the same tracker through real passes; these reach each gate directly.
/// </summary>
public class SubmitCoverageTrackerTests
{
    private static readonly object Owner = new();
    private static readonly FieldIdentifier FieldA = new(Owner, "A");
    private static readonly FieldIdentifier FieldB = new(Owner, "B");

    /// <summary>
    /// A tracker wired the way the engine wires one — the Task's store, a stubbed on-its-way
    /// predicate the test flips, and a resolver mapping an issue's path onto the shared owner —
    /// plus a two-rule selection and a helper that files a whole-selection answer, which is the
    /// smallest thing that lets a read compute fresh and hold.
    /// </summary>
    private sealed class Harness
    {
        private readonly RuleIdentity[] _rules = [new(new object()), new(new object())];

        public SetVerdictStore Store { get; } = new();

        public bool OnItsWay { get; set; }

        public SubmitCoverageTracker Tracker { get; }

        public Harness()
        {
            Tracker = new SubmitCoverageTracker(
                Store,
                () => OnItsWay,
                issue => new FieldIdentifier(Owner, issue.Path));
        }

        public Func<ValidationProfile, IReadOnlyList<RuleIdentity>> SelectRules => _ => _rules;

        public void AnswerWhole(
            int editStamp, ValidationProfile profile, params ValidationIssue[] issues)
        {
            Assert.True(Store.TryFile(
                [new SetVerdict([.. _rules], issues, editStamp, isProfileScoped: false, profile)],
                Store.Generation));
        }
    }

    // Serve route 1. Mutation this breaks: dropping the stamp match from the first serve route —
    // an answer served regardless of the stamp is unmarked, so it neither excludes the edited
    // field nor re-checks the promise it never needed.
    [Fact]
    public void The_held_answer_serves_at_the_stamp_and_profile_it_was_computed_for()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));

        // The field-set-change shape: the store empties and the version moves; the stamp does
        // not, so the held answer still describes the model as it stands.
        harness.Store.Clear();
        harness.Tracker.MoveVersion();

        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));
    }

    // Route 1 serves the whole held answer, error fields included — not merely the yes.
    [Fact]
    public void A_held_answers_error_fields_stay_excluded_when_served()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile, new ValidationIssue("A", "no"));
        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));

        harness.Store.Clear();
        harness.Tracker.MoveVersion();

        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));
    }

    // Serve route 2. Mutation this breaks: dropping the edited-field exclusion from the vouch —
    // the edited field would ride the held answer on the strength of a value it no longer holds.
    [Fact]
    public void An_edit_serves_the_held_answer_for_the_fields_it_did_not_touch()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));

        // The edit, as the engine delivers it: the field joins the edited record and the stamp
        // moves, with the promised re-answer still ahead.
        harness.OnItsWay = true;
        harness.Tracker.NoteEdit(FieldA);

        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 1, profile, harness.SelectRules));
        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 1, profile, harness.SelectRules));
    }

    // Mutation this breaks, jointly with the marking half of route 2: a served answer that is
    // never re-checked — or never marked as served — keeps vouching after the promise it was
    // served on is gone, and only the cache short-circuit's re-check can see that, because
    // nothing else re-keys the cache while a pass merely hangs.
    [Fact]
    public void A_served_answer_is_re_checked_on_every_read_and_drops_when_nothing_is_coming()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));

        harness.OnItsWay = true;
        harness.Tracker.NoteEdit(FieldA);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 1, profile, harness.SelectRules));

        // The promise evaporates; no coordinate moves. The next read must notice on its own.
        harness.OnItsWay = false;

        Assert.False(harness.Tracker.WouldPassSubmit(FieldB, 1, profile, harness.SelectRules));
    }

    // Mutation this breaks: an Abandon that leaves either serve route alive. The version moves
    // beside it in both halves because the engine's own abandon sites do exactly that — the
    // fault path's EndPass and the load's stamp bump — and a cache nothing re-keys would
    // otherwise keep answering without ever consulting the hold.
    [Fact]
    public void Abandon_kills_both_serve_routes_outright()
    {
        var profile = ValidationProfile.Submit;

        // Route 1: the field-set-change serve refuses once the hold is gone.
        var first = new Harness();
        first.AnswerWhole(0, profile);
        Assert.True(first.Tracker.WouldPassSubmit(FieldA, 0, profile, first.SelectRules));
        first.Store.Clear();
        first.Tracker.MoveVersion();
        first.Tracker.Abandon();
        first.Tracker.MoveVersion();
        Assert.False(first.Tracker.WouldPassSubmit(FieldA, 0, profile, first.SelectRules));

        // Route 2: a promise still standing serves nothing once the hold is gone.
        var second = new Harness();
        second.AnswerWhole(0, profile);
        Assert.True(second.Tracker.WouldPassSubmit(FieldB, 0, profile, second.SelectRules));
        second.OnItsWay = true;
        second.Tracker.NoteEdit(FieldA);
        Assert.True(second.Tracker.WouldPassSubmit(FieldB, 1, profile, second.SelectRules));
        second.Tracker.Abandon();
        second.Tracker.MoveVersion();
        Assert.False(second.Tracker.WouldPassSubmit(FieldB, 1, profile, second.SelectRules));
    }

    // Mutation this breaks: dropping the profile guard from the serve routes. The compare is by
    // reference, so even a profile of identical shape is not the one the answer was computed
    // under — an answer about one selection of rules never vouches for another, and equality of
    // NAME says nothing about what a settable options instance selects now.
    [Fact]
    public void A_different_profile_instance_never_serves()
    {
        var harness = new Harness();
        var held = ValidationProfile.Submit;
        var twin = ValidationProfile.Named(
            "Submit", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName);
        Assert.Equal(held, twin);
        Assert.NotSame(held, twin);

        harness.AnswerWhole(0, held);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 0, held, harness.SelectRules));

        harness.Store.Clear();
        harness.Tracker.MoveVersion();

        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 0, twin, harness.SelectRules));
    }

    // Mutation this breaks: a fresh hold that keeps the old edited-field record. A field edited
    // before an answer that has since covered it is not edited past THAT answer — leaving it
    // excluded would blank a field the current hold honestly vouches for.
    [Fact]
    public void A_fresh_hold_clears_the_edited_field_record()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));

        // FieldA's edit, then its re-answer landing: the whole selection answers at the new
        // stamp, so the next read earns a fresh hold that owes FieldA nothing.
        harness.Tracker.NoteEdit(FieldA);
        harness.AnswerWhole(1, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 1, profile, harness.SelectRules));

        // A later edit of FieldB serves across: FieldA rides the hold, FieldB is excluded.
        harness.OnItsWay = true;
        harness.Tracker.NoteEdit(FieldB);

        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 2, profile, harness.SelectRules));
        Assert.False(harness.Tracker.WouldPassSubmit(FieldB, 2, profile, harness.SelectRules));
    }

    // Mutation this breaks: dropping the stamp equality from the capability-less branch — a
    // whole-model answer counts exactly while its begin stamp is the current stamp, and a stale
    // one vouches for nothing however clean its fields look. The serve routes never apply on
    // this branch: with no verdicts to walk there is no remainder to serve across, so even a
    // standing promise serves nothing.
    [Fact]
    public void A_capability_less_answer_counts_exactly_while_its_begin_stamp_is_current()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.Tracker.RecordWholeModelAnswer(0, [FieldA]);

        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, selectRules: null));
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, selectRules: null));

        // The stamp moves; a promise stands. Neither serve route reaches this branch.
        harness.OnItsWay = true;

        Assert.False(harness.Tracker.WouldPassSubmit(FieldB, 1, profile, selectRules: null));
        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 1, profile, selectRules: null));
    }

    // Mutation this breaks: letting a selection throw escape the read, or letting the held
    // answer stand in for a selection that cannot be walked. The read is the render path, so
    // the throw must become "stale" here and surface through the next pass's fault policy.
    [Fact]
    public void A_throwing_selection_reads_as_stale_and_nothing_held_stands_in_for_it()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(0, profile);
        Assert.True(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));

        // Re-keys the cache so the next read recomputes instead of short-circuiting.
        harness.Tracker.MoveVersion();

        Assert.False(harness.Tracker.WouldPassSubmit(
            FieldA,
            0,
            profile,
            _ => throw new InvalidOperationException("typo'd ruleset")));
    }

    // The walk half of a fresh compute: a reused set's error resolves to its field through the
    // injected resolver, and only error severity blocks — an advisory on a field leaves its
    // vouch standing.
    [Fact]
    public void A_reused_sets_error_blocks_only_its_own_fields_vouch()
    {
        var harness = new Harness();
        var profile = ValidationProfile.Submit;

        harness.AnswerWhole(
            0,
            profile,
            new ValidationIssue("A", "no"),
            new ValidationIssue("B", "careful", ValidationSeverity.Warning));

        Assert.False(harness.Tracker.WouldPassSubmit(FieldA, 0, profile, harness.SelectRules));
        Assert.True(harness.Tracker.WouldPassSubmit(FieldB, 0, profile, harness.SelectRules));
    }
}
