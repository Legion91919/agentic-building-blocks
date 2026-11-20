using Agentic.Blocks.Audit;
using Agentic.Blocks.Core;
using Agentic.Blocks.Testing;
using Agentic.Blocks.Web;
using Xunit;

namespace Agentic.Blocks.Audit.Tests;

public class AuditLogTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly InMemoryAuditStore _store = new();

    private AuditLog Log() => new(_store, _clock, new CorrelationContext());

    [Fact]
    public async Task Append_stamps_time_sequence_and_hash()
    {
        var entry = (await Log().AppendAsync(new AuditRecord("u-1", "subscription.cancelled", "subscription", "s-1", "user request"))).Value;
        Assert.Equal(1, entry.Sequence);
        Assert.Equal(_clock.GetUtcNow(), entry.Timestamp);
        Assert.Equal(string.Empty, entry.PreviousHash);
        Assert.Equal(entry.ComputeHash(), entry.Hash);
        Assert.Equal("user request", entry.Reason);
    }

    [Fact]
    public async Task Append_captures_correlation_id()
    {
        using (CorrelationContext.Begin("corr-7"))
        {
            var entry = (await Log().AppendAsync(new AuditRecord("u-1", "a.b", "t", "1"))).Value;
            Assert.Equal("corr-7", entry.CorrelationId);
        }
    }

    [Fact]
    public async Task Entries_chain_to_previous_hash()
    {
        var log = Log();
        var first = (await log.AppendAsync(new AuditRecord("u-1", "a.b", "t", "1"))).Value;
        var second = (await log.AppendAsync(new AuditRecord("u-1", "a.c", "t", "1"))).Value;
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task Invalid_record_returns_field_errors()
    {
        var result = await Log().AppendAsync(new AuditRecord("", "a.b", "", "1", new string('x', 2001)));
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.True(result.Error.FieldErrors.ContainsKey("actor"));
        Assert.True(result.Error.FieldErrors.ContainsKey("entityType"));
        Assert.True(result.Error.FieldErrors.ContainsKey("reason"));
    }

    [Fact]
    public async Task Query_by_entity_is_newest_first_and_filtered()
    {
        var log = Log();
        await log.AppendAsync(new AuditRecord("u-1", "a.created", "order", "o-1"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await log.AppendAsync(new AuditRecord("u-2", "a.created", "order", "o-2"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await log.AppendAsync(new AuditRecord("u-1", "a.shipped", "order", "o-1"));
        var page = (await log.GetByEntityAsync("order", "o-1", new PageRequest())).Value;
        Assert.Equal(["a.shipped", "a.created"], page.Items.Select(e => e.Action).ToList());
    }

    [Fact]
    public async Task Query_by_actor_pages_with_cursor()
    {
        var log = Log();
        for (var i = 0; i < 5; i++) await log.AppendAsync(new AuditRecord("u-1", $"a.n{i}", "t", $"{i}"));
        await log.AppendAsync(new AuditRecord("u-2", "a.other", "t", "x"));
        var first = (await log.GetByActorAsync("u-1", new PageRequest(null, 2))).Value;
        var second = (await log.GetByActorAsync("u-1", new PageRequest(first.NextCursor, 2))).Value;
        var third = (await log.GetByActorAsync("u-1", new PageRequest(second.NextCursor, 2))).Value;
        Assert.Equal(["a.n4", "a.n3"], first.Items.Select(e => e.Action).ToList());
        Assert.Equal(["a.n2", "a.n1"], second.Items.Select(e => e.Action).ToList());
        Assert.Equal(["a.n0"], third.Items.Select(e => e.Action).ToList());
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task Bad_cursor_and_empty_filters_are_validation_errors()
    {
        var log = Log();
        Assert.Equal(ErrorType.Validation, (await log.GetByActorAsync("u-1", new PageRequest("@@@"))).Error.Type);
        Assert.Equal(ErrorType.Validation, (await log.GetByActorAsync(" ", new PageRequest())).Error.Type);
        Assert.Equal(ErrorType.Validation, (await log.GetByEntityAsync("t", "", new PageRequest())).Error.Type);
    }

    [Fact]
    public async Task Verify_passes_on_untouched_log()
    {
        var log = Log();
        for (var i = 0; i < 3; i++) await log.AppendAsync(new AuditRecord("u-1", "a.b", "t", $"{i}"));
        Assert.True((await log.VerifyIntegrityAsync()).IsSuccess);
    }

    [Fact]
    public async Task Verify_detects_tampering_at_storage_layer()
    {
        var log = Log();
        for (var i = 0; i < 3; i++) await log.AppendAsync(new AuditRecord("u-1", "a.b", "t", $"{i}"));
        _store.TamperForTest(2, e => e with { Reason = "edited later" });
        var result = await log.VerifyIntegrityAsync();
        Assert.Equal("audit.integrity_broken", result.Error.Code);
        Assert.Equal(2L, result.Error.Metadata["sequence"]);
    }

    [Fact]
    public async Task Store_rejects_out_of_order_sequence()
    {
        var entry = new AuditEntry { Sequence = 2, Id = Guid.NewGuid(), Timestamp = _clock.GetUtcNow(), Actor = "a", Action = "b", EntityType = "c", EntityId = "d", PreviousHash = "", Hash = "" };
        Assert.False(await _store.TryAppendAsync(entry));
    }

    [Fact]
    public async Task Concurrent_appends_produce_gap_free_valid_chain()
    {
        var log = Log();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => log.AppendAsync(new AuditRecord("u", "a.b", "t", $"{i}")))));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i).ToList(), results.Select(r => r.Value.Sequence).OrderBy(x => x).ToList());
        Assert.True((await log.VerifyIntegrityAsync()).IsSuccess);
    }

    [Fact]
    public void Public_api_has_no_update_or_delete()
    {
        var names = typeof(IAuditLog).GetMethods().Concat(typeof(IAuditStore).GetMethods()).Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Update", StringComparison.Ordinal) || n.Contains("Delete", StringComparison.Ordinal) || n.Contains("Remove", StringComparison.Ordinal));
    }
}
