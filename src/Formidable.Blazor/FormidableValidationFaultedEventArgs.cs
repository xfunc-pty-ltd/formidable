namespace Formidable.Blazor;

/// <summary>The arguments <see cref="IFormidableEngine.ValidationFaulted"/> carries: the exception a check threw, grown by init-only properties so a handler written against it keeps compiling.</summary>
public sealed class FormidableValidationFaultedEventArgs : EventArgs
{
    /// <summary>Creates arguments carrying <paramref name="exception"/>.</summary>
    /// <param name="exception">The exception the check threw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public FormidableValidationFaultedEventArgs(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <summary>The exception the check threw, the same instance and never a wrap, so a subscriber can correlate it with anything else that observed the failure.</summary>
    public Exception Exception { get; }
}
