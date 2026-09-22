namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableCss.Compute"/>'s tier ordering: errors beat warnings beat infos beat
/// plain valid, the advisory tiers share Valid's touched-or-modified gate rather than Invalid's
/// ungated one, and Pending appends onto whichever tier won rather than replacing it.
/// </summary>
public class FormidableCssComputeTests
{
    [Fact]
    public void Errors_beat_warnings()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: true, HasWarnings: true, HasInfos: false);

        Assert.Equal(classes.Invalid, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Warnings_beat_infos()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: true, HasInfos: true);

        Assert.Equal(classes.Warning, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Infos_alone_earn_info()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: false, HasInfos: true);

        Assert.Equal(classes.Info, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Advisories_need_touch()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: false, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: true, HasInfos: false);

        Assert.Equal(string.Empty, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Pending_appends_to_warning()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: true,
            HasErrors: false, HasWarnings: true, HasInfos: false);

        Assert.Equal("formidable-warning formidable-pending", FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Valid_untouched_by_the_new_tiers()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: false, HasInfos: false);

        Assert.Equal(classes.Valid, FormidableCss.Compute(state, classes));
    }

    // Valid alone consults WouldPassSubmit: a touched, issue-free field the engine cannot vouch
    // for wears no class rather than a confirmation it has not earned. The bit defaults to true,
    // which is what keeps every state built without an engine (the tests above included) on the
    // plain-valid branch.
    [Fact]
    public void Valid_requires_would_pass_submit()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: false, HasInfos: false, WouldPassSubmit: false);

        Assert.Equal(string.Empty, FormidableCss.Compute(state, classes));
    }

    // The advisory tiers ignore the bit deliberately: a disclosed warning is a fact about the
    // field regardless of what submit would say, so denying it alongside Valid would hide
    // information rather than withhold a promise.
    [Fact]
    public void Warnings_ignore_would_pass_submit()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: true, HasInfos: false, WouldPassSubmit: false);

        Assert.Equal(classes.Warning, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Untouched_unmodified_no_issues_is_empty()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: false, IsModified: false, IsValidating: false,
            HasErrors: false, HasWarnings: false, HasInfos: false);

        Assert.Equal(string.Empty, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Invalid_wins_when_both_touched_and_modified()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: true, IsValidating: false,
            HasErrors: true, HasWarnings: false, HasInfos: false);

        Assert.Equal(classes.Invalid, FormidableCss.Compute(state, classes));
    }

    [Fact]
    public void Pending_appends_to_invalid()
    {
        var classes = new FormidableCssClasses();
        var state = new FieldState(
            IsTouched: true, IsModified: true, IsValidating: true,
            HasErrors: true, HasWarnings: false, HasInfos: false);

        Assert.Equal("formidable-invalid formidable-pending", FormidableCss.Compute(state, classes));
    }
}
