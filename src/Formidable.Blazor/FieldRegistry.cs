using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Tracks which fields are currently rendered. Rendered field components and anchors register
/// on initialization and unregister on dispose, making the markup's conditional rendering the
/// source of truth for progressive disclosure: an issue whose field has no registration is
/// unrevealed and suppressed from inline display.
/// </summary>
public sealed class FieldRegistry
{
    private readonly Dictionary<FieldIdentifier, int> _counts = [];
    private readonly HashSet<FieldIdentifier> _kept = [];

    /// <summary>
    /// Registers a rendered field. Dispose the returned handle when the field leaves the
    /// render tree. With <paramref name="keepRegistered"/> the field stays revealed after
    /// disposal — for containers such as <c>Virtualize</c> that dispose rows scrolled out of
    /// view without the row ceasing to be part of the form. A later registration of the same
    /// field disposed without keepRegistered removes the retained entry — the latest disposal's
    /// intent wins.
    /// </summary>
    public FieldRegistration Register(FieldIdentifier field, bool keepRegistered = false)
    {
        _counts[field] = _counts.TryGetValue(field, out var count) ? count + 1 : 1;
        return new FieldRegistration(this, field, keepRegistered);
    }

    /// <summary>True when the field is currently rendered (or retained via keep-registered).</summary>
    public bool IsRevealed(FieldIdentifier field) => _counts.ContainsKey(field) || _kept.Contains(field);

    internal void Unregister(FieldIdentifier field, bool keepRegistered)
    {
        if (!_counts.TryGetValue(field, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _counts.Remove(field);
            if (keepRegistered)
            {
                _kept.Add(field);
            }
            else
            {
                _kept.Remove(field); // latest disposal intent wins: a non-kept dispose reverses an earlier keep
            }
        }
        else
        {
            _counts[field] = count - 1;
        }
    }
}
