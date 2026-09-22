namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the split between <see cref="FieldState"/>'s two engine-less construction shapes:
/// running the parameterless constructor — <c>new FieldState()</c>, with or without an object
/// initializer — honours the <see cref="FieldState.WouldPassSubmit"/> initializer, while
/// <c>default(FieldState)</c> bypasses constructors and zeroes every member, so a defaulted
/// state cannot vouch that a submit would pass.
/// </summary>
public class FieldStateTests
{
    [Fact]
    public void New_runs_the_would_pass_submit_initializer()
    {
        Assert.True(new FieldState().WouldPassSubmit);
    }

    [Fact]
    public void An_object_initializer_keeps_the_initializer_for_members_it_leaves_unset()
    {
        var state = new FieldState { IsTouched = true, HasErrors = true };

        Assert.True(state.WouldPassSubmit);
        Assert.True(state.IsTouched);
        Assert.True(state.HasErrors);
        Assert.False(state.IsModified);
    }

    [Fact]
    public void Default_zeroes_every_member_including_would_pass_submit()
    {
        var state = default(FieldState);

        Assert.False(state.WouldPassSubmit);
        Assert.False(state.IsTouched);
        Assert.False(state.IsModified);
        Assert.False(state.IsValidating);
        Assert.False(state.HasErrors);
        Assert.False(state.HasWarnings);
        Assert.False(state.HasInfos);
    }
}
