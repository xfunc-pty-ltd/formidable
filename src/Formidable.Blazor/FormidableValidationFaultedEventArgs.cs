namespace Formidable.Blazor;

/// <summary>
/// Arguments for <see cref="IFormValidationEngine.ValidationFaulted"/>: the exception the pass
/// failed with. Anything the event learns to say beyond it — which pass faulted, what scope it
/// covered — is added here as init-only properties, which a handler written before the addition
/// keeps compiling against and simply does not read; the event's delegate shape never changes
/// for it.
/// </summary>
public sealed class FormidableValidationFaultedEventArgs : EventArgs
{
    /// <summary>Creates arguments carrying <paramref name="exception"/>.</summary>
    /// <param name="exception">The exception the pass failed with.</param>
    public FormidableValidationFaultedEventArgs(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <summary>
    /// The exception the pass failed with — the instance itself, never a copy or a wrap, so a
    /// subscriber can correlate it with anything else that observed the same failure.
    /// </summary>
    public Exception Exception { get; }
}
