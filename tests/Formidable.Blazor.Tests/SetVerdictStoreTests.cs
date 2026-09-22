namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="SetVerdictStore"/>'s contract at its own seam, without a pass anywhere: a
/// filed set serves only a selection that contains it whole, at the stamp it answered for; a
/// submit-shaped plan (<c>executeAll</c>) serves nothing by fiat; a write planned against a
/// stale generation — or, on the probe's overload, across a moved edit stamp — is refused
/// whole; clearing forgets every verdict and moves the generation; and filing supersedes every
/// stored set the new one overlaps or out-dates, the per-rule index following the drop. The
/// engine-level reuse pins exercise the same store through passes; these reach each gate
/// directly, one condition per test.
/// </summary>
public class SetVerdictStoreTests
{
    private static readonly ValidationProfile Profile = ValidationProfile.Submit;

    private static RuleIdentity Rule() => new(new object());

    private static SetVerdict Verdict(RuleIdentity[] rules, int editStamp) =>
        new([.. rules], [], editStamp, isProfileScoped: false, Profile);

    [Fact]
    public void A_filed_set_is_served_to_a_selection_that_contains_it()
    {
        var store = new SetVerdictStore();
        var (a, b) = (Rule(), Rule());
        var verdict = Verdict([a, b], editStamp: 3);

        Assert.True(store.TryFile([verdict], store.Generation));

        var plan = store.Plan([a, b], executeAll: false, editStamp: 3, Profile);

        Assert.Same(verdict, Assert.Single(plan.Reused));
        Assert.Empty(plan.Remainder);
    }

    [Fact]
    public void A_set_the_selection_straddles_is_not_served()
    {
        var store = new SetVerdictStore();
        var (a, b) = (Rule(), Rule());
        store.TryFile([Verdict([a, b], editStamp: 3)], store.Generation);

        // The set's issues belong to both rules together, so a selection holding only one of
        // them has no way to strip the other's share: the whole set must re-run.
        var plan = store.Plan([a], executeAll: false, editStamp: 3, Profile);

        Assert.Empty(plan.Reused);
        Assert.Equal(new[] { a }, plan.Remainder);
    }

    [Fact]
    public void Execute_all_serves_nothing_by_fiat()
    {
        var store = new SetVerdictStore();
        var (a, b) = (Rule(), Rule());
        store.TryFile([Verdict([a, b], editStamp: 3)], store.Generation);

        var plan = store.Plan([a, b], executeAll: true, editStamp: 3, Profile);

        Assert.Empty(plan.Reused);
        Assert.Equal(new[] { a, b }, plan.Remainder);
    }

    [Fact]
    public void A_stale_generation_files_nothing_and_answers_false()
    {
        var store = new SetVerdictStore();
        var a = Rule();
        var planned = store.Generation;
        store.Clear();

        Assert.False(store.TryFile([Verdict([a], editStamp: 3)], planned));

        var plan = store.Plan([a], executeAll: false, editStamp: 3, Profile);
        Assert.Empty(plan.Reused);
        Assert.Equal(new[] { a }, plan.Remainder);
    }

    [Fact]
    public void The_probe_overload_refuses_a_batch_whose_edit_stamp_has_moved()
    {
        var store = new SetVerdictStore();
        var a = Rule();

        Assert.False(store.TryFile(
            [Verdict([a], editStamp: 3)], store.Generation, plannedEditStamp: 3, currentEditStamp: 4));
        Assert.Empty(store.Plan([a], executeAll: false, editStamp: 3, Profile).Reused);

        // Agreeing stamps take the same route the two-argument overload takes.
        Assert.True(store.TryFile(
            [Verdict([a], editStamp: 3)], store.Generation, plannedEditStamp: 3, currentEditStamp: 3));
        Assert.Single(store.Plan([a], executeAll: false, editStamp: 3, Profile).Reused);
    }

    [Fact]
    public void Clear_forgets_every_verdict_and_moves_the_generation()
    {
        var store = new SetVerdictStore();
        var a = Rule();
        store.TryFile([Verdict([a], editStamp: 3)], store.Generation);
        var before = store.Generation;

        store.Clear();

        Assert.NotEqual(before, store.Generation);
        var plan = store.Plan([a], executeAll: false, editStamp: 3, Profile);
        Assert.Empty(plan.Reused);
        Assert.Equal(new[] { a }, plan.Remainder);
    }

    [Fact]
    public void Filing_drops_every_stored_set_sharing_a_rule_with_the_new_one()
    {
        var store = new SetVerdictStore();
        var (a, b, c) = (Rule(), Rule(), Rule());
        store.TryFile([Verdict([a, b], editStamp: 3)], store.Generation);

        // The second set shares b with the first, so the first goes whole: were both kept, a
        // plan could serve b's issues twice, once from each.
        var second = Verdict([b, c], editStamp: 3);
        Assert.True(store.TryFile([second], store.Generation));

        var plan = store.Plan([a, b, c], executeAll: false, editStamp: 3, Profile);
        Assert.Same(second, Assert.Single(plan.Reused));
        Assert.Equal(new[] { a }, plan.Remainder);
    }

    [Fact]
    public void A_dropped_set_never_serves_through_a_surviving_index_entry()
    {
        var store = new SetVerdictStore();
        var (a, b) = (Rule(), Rule());
        store.TryFile([Verdict([a, b], editStamp: 3)], store.Generation);

        // Filing [b] drops the [a, b] set; the index entry a held for it must go with it, or a
        // later plan would follow a to the dropped set and serve b's issues beside the set that
        // superseded them.
        var second = Verdict([b], editStamp: 3);
        Assert.True(store.TryFile([second], store.Generation));

        var plan = store.Plan([a, b], executeAll: false, editStamp: 3, Profile);
        Assert.Same(second, Assert.Single(plan.Reused));
        Assert.Equal(new[] { a }, plan.Remainder);
    }

    [Fact]
    public void Filing_drops_every_stored_set_answering_an_older_stamp()
    {
        var store = new SetVerdictStore();
        var (b, c) = (Rule(), Rule());
        store.TryFile([Verdict([b], editStamp: 3)], store.Generation);

        // A landing at stamp 4 supersedes the model state stamp 3 answered for, so the stamp-3
        // set goes even though the new set shares no rule with it.
        store.TryFile([Verdict([c], editStamp: 4)], store.Generation);

        var plan = store.Plan([b], executeAll: false, editStamp: 3, Profile);
        Assert.Empty(plan.Reused);
        Assert.Equal(new[] { b }, plan.Remainder);
    }

    [Fact]
    public void A_null_or_empty_batch_files_nothing_and_answers_false()
    {
        var store = new SetVerdictStore();
        var a = Rule();

        Assert.False(store.TryFile(null, store.Generation));
        Assert.False(store.TryFile([], store.Generation));
        Assert.False(store.TryFile(null, store.Generation, plannedEditStamp: 3, currentEditStamp: 3));
        Assert.False(store.TryFile([], store.Generation, plannedEditStamp: 3, currentEditStamp: 3));

        Assert.Empty(store.Plan([a], executeAll: false, editStamp: 3, Profile).Reused);
    }
}
