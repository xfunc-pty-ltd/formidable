using System.Diagnostics;

namespace Formidable.Tests;

public class FluentValidationInspectionSurfaceTests
{
    /// <summary>
    /// The guard's positive control: every member the inspection walk reads by name exists on
    /// the FluentValidation this suite resolves, so the predicate vouches, the cached answer
    /// agrees, and <c>CanInspectRules</c> keeps its shape-only answer. A predicate looking for
    /// a member FluentValidation does not carry fails here first — together with every
    /// inspection pin in the suite, since the readers claim nothing once the guard trips.
    /// </summary>
    [Fact]
    public void The_inspection_surface_is_intact_on_the_resolved_FluentValidation()
    {
        Assert.True(FluentValidationInspectionSurface.Verify());
        Assert.True(FluentValidationInspectionSurface.Intact);
    }

    /// <summary>
    /// The diagnostic belongs to the tripped path alone: an intact surface writes nothing,
    /// however many validators consult the guard. A predicate that stops finding its members
    /// writes the line this listener catches, and that line — not the returned answer — is
    /// what fails this test.
    /// </summary>
    [Fact]
    public void An_intact_surface_writes_no_trace_diagnostic()
    {
        using var writer = new StringWriter();
        using var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);
        try
        {
            FluentValidationInspectionSurface.Verify();
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.DoesNotContain("rule inspection", writer.ToString());
    }
}
