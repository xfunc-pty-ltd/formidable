using System.Text;

namespace Formidable.Blazor;

/// <summary>Shared field CSS class rule: errors win; touched/modified without errors is valid; pending appends while validating.</summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined class string for a field state.</summary>
    public static string Compute(FieldState state, FormidableCssOptions options)
    {
        var builder = new StringBuilder();

        if (state.HasErrors)
        {
            builder.Append(options.Invalid);
        }
        else if (state.IsTouched || state.IsModified)
        {
            builder.Append(options.Valid);
        }

        if (state.IsValidating)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(options.Pending);
        }

        return builder.ToString();
    }
}
