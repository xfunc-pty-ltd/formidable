using System.Diagnostics;
using FluentValidation;
using FluentValidation.Validators;

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
        Assert.NotNull(FluentValidationInspectionSurface.Verify());
        Assert.True(FluentValidationInspectionSurface.Intact);
    }

    /// <summary>
    /// The check keeps the four members it found. Each is declared on the open generic type it was
    /// looked up on, and the child reading matches the adaptor's two to each live adaptor's closed
    /// type instead of looking them up again.
    /// </summary>
    // Mutation this breaks: Verify keeps null where it found all four members, writing no Trace
    // line. The child-reading tests in ChildAdaptorReadingTests fail with it, because a surface
    // that keeps no members reads as not intact and the readers then claim nothing.
    [Fact]
    public void The_surface_keeps_the_members_it_verified()
    {
        var members = FluentValidationInspectionSurface.Members;

        Assert.NotNull(members);
        Assert.Equal(typeof(ChildValidatorAdaptor<,>), members.GetValidator.DeclaringType);
        Assert.Equal(typeof(ChildValidatorAdaptor<,>), members.RuleSets.DeclaringType);
        Assert.Equal(typeof(ICollectionRule<,>), members.Filter.DeclaringType);
        Assert.Equal(typeof(ICollectionRule<,>), members.AsyncFilter.DeclaringType);
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
