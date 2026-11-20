using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentic.Blocks.Core;

/// <summary>Something that happened in the domain that other parts of the system may react to.</summary>
public interface IDomainEvent
{
    /// <summary>Unique id of this event (use it to de-duplicate in handlers).</summary>
    Guid EventId { get; }

    /// <summary>When the event happened (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Gives access to the correlation id of the current request or message, if any.</summary>
public interface ICorrelationContext
{
    /// <summary>The current correlation id, or null outside a request or message.</summary>
    string? CorrelationId { get; }
}

/// <summary>
/// Naming convention for traces and metrics. Every block owns one <see cref="ActivitySource"/> and one
/// <see cref="Meter"/> named <c>Agentic.Blocks.&lt;Block&gt;</c>; instruments are lower-case and dot-separated:
/// <c>agentic.blocks.&lt;block&gt;.&lt;name&gt;</c>. Subscribe to everything with <see cref="Wildcard"/>.
/// </summary>
public static class BlocksTelemetry
{
    /// <summary>Common prefix of every source and meter.</summary>
    public const string Prefix = "Agentic.Blocks";

    /// <summary>Wildcard for OpenTelemetry <c>AddSource</c>/<c>AddMeter</c>.</summary>
    public const string Wildcard = "Agentic.Blocks.*";

    /// <summary>Source and meter name for a block, for example <c>Agentic.Blocks.Outbox</c>.</summary>
    public static string Name(string block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(block);
        return $"{Prefix}.{block}";
    }

    /// <summary>Instrument name, for example <c>agentic.blocks.outbox.dispatched</c>.</summary>
    public static string InstrumentName(string block, string instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
#pragma warning disable CA1308 // OpenTelemetry instrument names are lower case by convention.
        return $"agentic.blocks.{block.ToLowerInvariant()}.{instrument.ToLowerInvariant()}";
#pragma warning restore CA1308
    }

    /// <summary>Creates the activity source for a block.</summary>
    public static ActivitySource CreateActivitySource(string block) =>
        new(Name(block), typeof(BlocksTelemetry).Assembly.GetName().Version?.ToString(3));

    /// <summary>Creates the meter for a block.</summary>
    public static Meter CreateMeter(string block) =>
        new(Name(block), typeof(BlocksTelemetry).Assembly.GetName().Version?.ToString(3));
}
