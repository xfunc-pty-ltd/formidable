using Formidable.Introspection;

namespace Formidable.Tests;

public class PropertyPathTests
{
    [Fact]
    public void Single_property_parses()
    {
        Assert.True(PropertyPath.TryParse("Description", out var segments));
        Assert.Equal([PathSegment.Property("Description")], segments);
    }

    [Fact]
    public void Dotted_path_parses()
    {
        Assert.True(PropertyPath.TryParse("Customer.Address.City", out var segments));
        Assert.Equal(
            [PathSegment.Property("Customer"), PathSegment.Property("Address"), PathSegment.Property("City")],
            segments);
    }

    [Fact]
    public void Indexed_path_parses()
    {
        Assert.True(PropertyPath.TryParse("LineItems[0].Sku", out var segments));
        Assert.Equal(
            [PathSegment.Property("LineItems"), PathSegment.Indexer("0"), PathSegment.Property("Sku")],
            segments);
    }

    [Fact]
    public void Nested_indexers_parse()
    {
        Assert.True(PropertyPath.TryParse("Students[2].Fees[10].Amount", out var segments));
        Assert.Equal(
            [
                PathSegment.Property("Students"), PathSegment.Indexer("2"),
                PathSegment.Property("Fees"), PathSegment.Indexer("10"),
                PathSegment.Property("Amount")
            ],
            segments);
    }

    [Fact]
    public void Dictionary_key_indexer_parses()
    {
        Assert.True(PropertyPath.TryParse("Attributes[colour]", out var segments));
        Assert.Equal([PathSegment.Property("Attributes"), PathSegment.Indexer("colour")], segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Items[")]
    [InlineData("Items[0")]
    [InlineData("Items]0[")]
    [InlineData("Items..Sku")]
    [InlineData(".Items")]
    [InlineData("Items[0]Sku")]
    [InlineData("Items.")]
    [InlineData("Items[]")]
    [InlineData("[0].Items")]
    public void Malformed_paths_return_false(string path)
    {
        Assert.False(PropertyPath.TryParse(path, out _));
    }

    [Fact]
    public void Trailing_indexer_is_valid()
    {
        Assert.True(PropertyPath.TryParse("LineItems[3]", out var segments));
        Assert.Equal([PathSegment.Property("LineItems"), PathSegment.Indexer("3")], segments);
    }

    [Fact]
    public void A_megabyte_of_dotted_segments_parses_without_rescanning_the_remainder()
    {
        // A property segment ends at the first '.' or '[', so finding that boundary must be
        // one scan, not one per separator kind: a bracket-free path searched for a '[' that is
        // never there re-reads the whole remainder per segment, which is quadratic. The size
        // is reachable — FormValidationEngine.Resolve parses the paths a server response
        // carries, verbatim, so the input is whatever a 400 body says it is. The budget is
        // deliberately loose: a single-scan parse of this input is milliseconds, and the
        // rescanning one misses it by seconds.
        var path = string.Join('.', Enumerable.Repeat("ab", 350_000));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var parsed = PropertyPath.TryParse(path, out var segments);
        stopwatch.Stop();

        Assert.True(parsed);
        Assert.Equal(350_000, segments.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Parsing {path.Length} bracket-free characters took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
