using Agentic.Blocks.Core;
using Xunit;

namespace Agentic.Blocks.Core.Tests;

public class PaginationTests
{
    private static readonly string[] Items = ["a", "b", "c", "d", "e"];

    [Fact]
    public void Walks_all_pages_without_gaps_or_duplicates()
    {
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = CursorPagination.Page(Items, x => x, new PageRequest(cursor, 2)).Value;
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(Items.ToList(), seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public void Descending_order_is_supported()
    {
        var first = CursorPagination.Page(Items, x => x, new PageRequest(null, 2), descending: true).Value;
        Assert.Equal(["e", "d"], first.Items);
        var second = CursorPagination.Page(Items, x => x, new PageRequest(first.NextCursor, 2), descending: true).Value;
        Assert.Equal(["c", "b"], second.Items);
    }

    [Fact]
    public void Last_page_has_no_cursor()
    {
        var page = CursorPagination.Page(Items, x => x, new PageRequest(null, 5)).Value;
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Insert_before_cursor_does_not_shift_next_page()
    {
        var list = new List<string> { "b", "d", "f" };
        var first = CursorPagination.Page(list, x => x, new PageRequest(null, 1)).Value;
        list.Add("a");
        var second = CursorPagination.Page(list, x => x, new PageRequest(first.NextCursor, 1)).Value;
        Assert.Equal(["d"], second.Items);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 100)]
    [InlineData(null, 20)]
    public void Limit_is_clamped(int? requested, int expected)
    {
        Assert.Equal(expected, new PageRequest(null, requested).Limit);
    }

    [Fact]
    public void Malformed_cursor_is_validation_error()
    {
        var result = CursorPagination.Page(Items, x => x, new PageRequest("!!not base64!!", 2));
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void Cursor_round_trips_and_is_url_safe()
    {
        var cursor = CursorPagination.Encode("0000000001|ab/c+?");
        Assert.DoesNotContain("/", cursor);
        Assert.DoesNotContain("+", cursor);
        Assert.DoesNotContain("=", cursor);
        Assert.True(CursorPagination.TryDecode(cursor, out var key));
        Assert.Equal("0000000001|ab/c+?", key);
    }

    [Fact]
    public void Time_key_sorts_chronologically()
    {
        var early = CursorPagination.TimeKey(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "z");
        var late = CursorPagination.TimeKey(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "a");
        Assert.True(string.CompareOrdinal(early, late) < 0);
    }
}

public class TelemetryTests
{
    [Fact]
    public void Names_follow_convention()
    {
        Assert.Equal("Agentic.Blocks.Outbox", BlocksTelemetry.Name("Outbox"));
        Assert.Equal("agentic.blocks.outbox.dispatched", BlocksTelemetry.InstrumentName("Outbox", "Dispatched"));
        using var source = BlocksTelemetry.CreateActivitySource("Web");
        Assert.Equal("Agentic.Blocks.Web", source.Name);
        using var meter = BlocksTelemetry.CreateMeter("Web");
        Assert.StartsWith(BlocksTelemetry.Prefix, meter.Name);
    }
}
