using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Records every <see cref="IFormidableFieldOrderService"/> call, standing in for the JS-backed
/// service so host tests assert against the seam instead of driving a browser. Answers with
/// <see cref="Result"/> when one is set — a test that means a specific document order — and
/// otherwise with the fields it was handed, reversed.
/// </summary>
public sealed class RecordingFieldOrderService : IFormidableFieldOrderService
{
    /// <summary>The field lists handed over, one entry per call, in call order.</summary>
    public List<IReadOnlyList<FieldIdentifier>> Requests { get; } = [];

    /// <summary>The order to answer with, or null to answer with the request reversed.</summary>
    public IReadOnlyList<FieldIdentifier>? Result { get; set; }

    /// <summary>Thrown instead of answering, for the failure paths the host has to survive.</summary>
    public Exception? Fault { get; set; }

    /// <summary>
    /// Clears <see cref="Fault"/> after it is thrown once, so the call after the failure succeeds
    /// — the shape a retry is only observable against.
    /// </summary>
    public bool FaultOnce { get; set; }

    /// <summary>
    /// Answers with <see langword="null"/> once — no order at all, the seam's other way of saying
    /// it could not answer — and normally thereafter, so the retry after one is observable. Distinct
    /// from both neighbouring shapes: an unset <see cref="Result"/> answers with the request
    /// reversed, which is an order, and <see cref="Fault"/> never answers at all.
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
