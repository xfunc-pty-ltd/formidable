namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="RetainedLiveReport.IsCurrentFor"/> is a conjunction of two independent checks, and
/// an engine-level scenario proving one side false does not by itself prove the other side is
/// still being evaluated — a report can fail to reach a refresh for reasons that have nothing to
/// do with the stamp half of the check. These pin each conjunct directly, against the type alone.
/// </summary>
public class RetainedLiveReportTests
{
    [Fact]
    public void IsCurrentFor_is_true_when_the_stamp_and_the_profile_both_match()
    {
        var live = ValidationProfile.Draft;
        var report = new RetainedLiveReport(ValidationReport.Empty, EditStamp: 3, live);

        Assert.True(report.IsCurrentFor(3, live));
    }

    [Fact]
    public void IsCurrentFor_is_false_when_the_stamp_has_moved_on()
    {
        var live = ValidationProfile.Draft;
        var report = new RetainedLiveReport(ValidationReport.Empty, EditStamp: 3, live);

        // An edit landed after this report was taken - the model it answers for is no longer the
        // model as it now stands, regardless of which profile it ran under.
        Assert.False(report.IsCurrentFor(4, live));
    }

    [Fact]
    public void IsCurrentFor_is_false_when_the_profile_has_changed()
    {
        var report = new RetainedLiveReport(ValidationReport.Empty, EditStamp: 3, ValidationProfile.Draft);

        // Same edit stamp, but the live profile in force is no longer the one this report ran
        // under - subtracting against it would drop or duplicate rules the report never covered.
        Assert.False(report.IsCurrentFor(3, ValidationProfile.Submit));
    }
}
