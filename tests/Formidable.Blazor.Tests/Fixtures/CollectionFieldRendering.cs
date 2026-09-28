using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>Renders a collection the way a page binds one, for the tests that edit it through its field's context.</summary>
internal static class CollectionFieldRendering
{
    /// <summary>
    /// Renders <paramref name="accessor"/>'s collection wrapped in a <c>FormidableField</c>, with a
    /// <c>FormidableCollectionMessage</c> for the collection's own rule, and hands the field's
    /// context out through <paramref name="captured"/> at each render.
    /// </summary>
    /// <param name="context">The test context to render in.</param>
    /// <param name="model">The form's model.</param>
    /// <param name="accessor">The collection, as the page binds it.</param>
    /// <param name="captured">Receives the field's context.</param>
    /// <returns>The rendered form.</returns>
    public static IRenderedComponent<FormidableForm<TModel>> RenderCollectionField<TModel, TValue>(
        this BunitContext context,
        TModel model,
        Expression<Func<TValue>> accessor,
        StrongBox<FormidableFieldContext> captured)
        where TModel : class
    {
        var cut = context.Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<TValue>>(0);
                inner.AddComponentParameter(1, "For", accessor);
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => content =>
                {
                    captured.Value = ctx;
                    content.OpenComponent<FormidableCollectionMessage<TValue>>(0);
                    content.AddComponentParameter(1, "For", accessor);
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<TModel>>();
    }
}
