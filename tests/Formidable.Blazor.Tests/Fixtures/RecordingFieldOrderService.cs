using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Stands in for the service that reads the fields' document order through JavaScript. It records
/// each request, and answers with <see cref="Result"/> when that is set, or otherwise with the
/// fields it was handed, reversed.
/// </summary>
public sealed class RecordingFieldOrderService : IFormidableFieldOrderService
{
    /// <summary>The field lists the form asked about, one entry per request, in order.</summary>
    public List<IReadOnlyList<FieldIdentifier>> Requests { get; } = [];

    /// <summary>The order to answer with, or null to answer with the request reversed.</summary>
    public IReadOnlyList<FieldIdentifier>? Result { get; set; }

    /// <summary>Thrown instead of an answer, to stand in for a browser call that failed.</summary>
    public Exception? Fault { get; set; }

    /// <summary>
    /// Clears <see cref="Fault"/> after it is thrown once, so the next request is answered.
    /// </summary>
    public bool FaultOnce { get; set; }

    /// <summary>
    /// Answers <see langword="null"/> (no order could be resolved) to the next request that
    /// <see cref="Fault"/> does not throw on, then answers as usual. An unset <see cref="Result"/>
    /// differs: it still answers with an order.
    /// </summary>
    public bool AnswerWithNothingOnce { get; set; }

    public ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields)
    {
        Requests.Add(fields);

        if (Fault is { } fault)
        {
            if (FaultOnce)
            {
                Fault = null;
            }

            throw fault;
        }

        if (AnswerWithNothingOnce)
        {
            AnswerWithNothingOnce = false;
            return ValueTask.FromResult<IReadOnlyList<FieldIdentifier>?>(null);
        }

        return ValueTask.FromResult<IReadOnlyList<FieldIdentifier>?>(Result ?? fields.Reverse().ToList());
    }
}
