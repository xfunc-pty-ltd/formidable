namespace Formidable.Tests.Fixtures;

public sealed class TestOrder
{
    public string Description { get; set; } = string.Empty;
    public TestCustomer? Customer { get; set; }
    public List<TestLineItem> LineItems { get; set; } = [];
    public Dictionary<string, string> Attributes { get; set; } = [];
}

public sealed class TestCustomer
{
    public string Name { get; set; } = string.Empty;
    public TestAddress? Address { get; set; }
}

public sealed class TestAddress
{
    public string City { get; set; } = string.Empty;
}

public sealed class TestLineItem
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
