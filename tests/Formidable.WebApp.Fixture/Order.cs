namespace Formidable.WebApp.Fixture;

/// <summary>An order with a description and its lines: the smallest model whose server reply can
/// name a field the page has hidden, a line inside a collapsed list.</summary>
public sealed class Order
{
    public string Description { get; set; } = string.Empty;

    public List<OrderLine> Lines { get; set; } = [new OrderLine()];
}

/// <summary>One line of an <see cref="Order"/>.</summary>
public sealed class OrderLine
{
    public string Sku { get; set; } = string.Empty;
}
