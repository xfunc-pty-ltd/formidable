namespace Formidable.Shared;

/// <summary>Marks an exception as one of Formidable's own refusals of how an app is set up, which fail every time they are reached, so a form can tell it from a rule that failed.</summary>
// An Exception.Data entry rather than an exception type of its own: the core package and the
// Blazor package share no internals, and a public type would be surface to support. The caller
// still catches the type each refusal documents.
internal static class FormidableConfigurationError
{
    /// <summary>The <see cref="System.Exception.Data"/> key the mark is stored under.</summary>
    internal const string DataKey = "Formidable.ConfigurationError";

    /// <summary>Marks <paramref name="exception"/> as a configuration error and returns it, for a <c>throw</c>.</summary>
    /// <typeparam name="TException">The exception's type.</typeparam>
    /// <param name="exception">The refusal about to be thrown.</param>
    /// <returns><paramref name="exception"/>, marked.</returns>
    internal static TException Mark<TException>(TException exception)
        where TException : System.Exception
    {
        exception.Data[DataKey] = true;
        return exception;
    }

    /// <summary>Whether <paramref name="exception"/> carries the mark.</summary>
    /// <param name="exception">The exception a check threw.</param>
    /// <returns><see langword="true"/> for one of Formidable's own refusals.</returns>
    internal static bool IsMarked(System.Exception exception) => exception.Data[DataKey] is true;
}
