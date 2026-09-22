namespace Formidable.Tutorial.Services;

/// <summary>
/// Stands in for a real directory lookup — the kind of check only a server can answer, because
/// the browser has no view of who else has already registered.
/// </summary>
public interface IEmailDirectory
{
    Task<bool> IsTakenAsync(string email, CancellationToken ct);
}

/// <summary>
/// An in-memory <see cref="IEmailDirectory"/> with one address already taken, and a delay standing
/// in for the network round trip a real lookup would cost.
/// </summary>
public sealed class InMemoryEmailDirectory : IEmailDirectory
{
    private static readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase)
    {
        "taken@example.com",
    };

    public async Task<bool> IsTakenAsync(string email, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        return Taken.Contains(email);
    }
}
