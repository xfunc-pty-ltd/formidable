namespace Formidable.Tests;

/// <summary>
/// Pins how <see cref="ValidationIssue.State"/> participates in the record's synthesized
/// equality: through <c>object.Equals</c>, which is reference equality for a state type that
/// does not override <c>Equals</c> — the usual shape of a <c>WithState(...)</c> payload.
/// </summary>
public class ValidationIssueTests
{
    [Fact]
    public void State_participates_in_equality_through_object_equals()
    {
        var stateA = new object();
        var stateB = new object();

        // Two otherwise-identical issues: distinct state instances that do not override
        // Equals compare by reference, so the issues are unequal...
        Assert.NotEqual(
            new ValidationIssue("Path", "Message", State: stateA),
            new ValidationIssue("Path", "Message", State: stateB));

        // ...while the same instance, and no state at all, compare equal...
        Assert.Equal(
            new ValidationIssue("Path", "Message", State: stateA),
            new ValidationIssue("Path", "Message", State: stateA));
        Assert.Equal(
            new ValidationIssue("Path", "Message"),
            new ValidationIssue("Path", "Message"));

        // ...and a state type overriding Equals brings its own semantics: equal-by-value
        // strings in distinct instances still compare equal.
        Assert.Equal(
            new ValidationIssue("Path", "Message", State: new string("s".ToCharArray())),
            new ValidationIssue("Path", "Message", State: new string("s".ToCharArray())));
    }
}
