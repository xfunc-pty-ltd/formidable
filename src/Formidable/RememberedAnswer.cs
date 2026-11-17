namespace Formidable;

/// <summary>A yes-or-no answer worked out on the first ask and kept in an <see cref="int"/> field the caller owns.</summary>
// An int, so each read and each write of the field is one whole operation and never torn. Two
// threads asking at once both work the answer out and write the same value.
internal static class RememberedAnswer
{
    private const int Unread = 0;
    private const int Absent = 1;
    private const int Present = 2;

    /// <summary>The answer kept in <paramref name="slot"/>, worked out by <paramref name="work"/> on the first ask.</summary>
    /// <typeparam name="TState">What the work reads.</typeparam>
    /// <param name="slot">The caller's field, zero until the first ask writes the answer there.</param>
    /// <param name="state">What the work reads, handed through so the work can be a static lambda.</param>
    /// <param name="work">Works the answer out; called only while the field is still zero.</param>
    /// <returns>The kept answer.</returns>
    internal static bool Read<TState>(ref int slot, TState state, Func<TState, bool> work)
    {
        var known = slot;
        if (known == Unread)
        {
            known = work(state) ? Present : Absent;
            slot = known;
        }

        return known == Present;
    }
}
