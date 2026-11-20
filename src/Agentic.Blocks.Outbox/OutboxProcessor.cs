using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Outbox;

/// <summary>Options for the outbox dispatcher.</summary>
public sealed class OutboxOptions
{
    /// <summary>Attempts before a message is dead-lettered. Default 8.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Delay after the first failure; doubles each attempt. Default 2 seconds.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for the back-off delay. Default 5 minutes.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Random spread of each delay, 0..1 (0.2 = plus or minus 20%). Default 0.2. Use 0 in tests.</summary>
    public double JitterFactor { get; set; } = 0.2;

    /// <summary>Messages per poll. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Idle poll interval (commits wake the dispatcher sooner). Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a leased message is hidden from other dispatchers. Default 1 minute.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Run the background dispatcher. Default true; turn off in tests and drive <see cref="OutboxProcessor"/> yourself.</summary>
    public bool RunDispatcher { get; set; } = true;

    /// <summary>The back-off delay before attempt <paramref name="failedAttempts"/> + 1, without jitter.</summary>
    public TimeSpan BackoffFor(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 30);
        var ticks = Math.Min(BaseDelay.Ticks * Math.Pow(2, exponent), MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}

/// <summary>Leases due messages and runs their handlers once. The background dispatcher calls it in a loop; tests call it directly.</summary>
public sealed partial class OutboxProcessor
{
    private readonly IOutboxStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxTypeRegistry _registry;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxMetrics _metrics;

    internal OutboxProcessor(IOutboxStore store, IServiceScopeFactory scopes, OutboxTypeRegistry registry, IOptions<OutboxOptions> options,
        TimeProvider time, ILogger<OutboxProcessor> logger, OutboxMetrics metrics)
    {
        _store = store;
        _scopes = scopes;
        _registry = registry;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Processes one batch of due messages. Returns how many were attempted.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batch = await _store.LeaseDueAsync(_time.GetUtcNow(), _options.BatchSize, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        foreach (var message in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return batch.Count;
    }

    private async Task ProcessOneAsync(OutboxMessage message, CancellationToken ct)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = message.CorrelationId, ["OutboxMessageId"] = message.Id });
        var type = _registry.TypeOf(message.Type);
        if (type is null)
        {
            await DeadLetterAsync(message, $"No handler is registered for message type '{message.Type}'.", ct).ConfigureAwait(false);
            return;
        }

        var attempt = message.Attempts + 1;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<IOutbox>();
            if (session is OutboxSession s) s.CorrelationOverride = message.CorrelationId;
            var context = new OutboxMessageContext(message.Id, message.Type, attempt, message.CorrelationId, message.CreatedAt);
            var handled = await OutboxTypeRegistry.InvokeHandlersAsync(scope.ServiceProvider, type, message.Payload, context, ct).ConfigureAwait(false);
            if (handled == 0)
            {
                await DeadLetterAsync(message, $"No handler is registered for message type '{message.Type}'.", ct).ConfigureAwait(false);
                return;
            }

            await session.CommitAsync(ct).ConfigureAwait(false);
            await _store.MarkSucceededAsync(message.Id, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            _metrics.Dispatched.Add(1);
        }
        catch (JsonException ex)
        {
            await DeadLetterAsync(message, $"Payload could not be deserialized: {ex.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            if (attempt >= _options.MaxAttempts)
            {
                await DeadLetterAsync(message, error, ct).ConfigureAwait(false);
                return;
            }

            var next = _time.GetUtcNow() + WithJitter(_options.BackoffFor(attempt));
            await _store.MarkFailedAsync(message.Id, error, next, CancellationToken.None).ConfigureAwait(false);
            _metrics.Failed.Add(1);
            LogRetry(_logger, message.Type, message.Id, attempt, next, ex);
        }
    }

    private async Task DeadLetterAsync(OutboxMessage message, string reason, CancellationToken ct)
    {
        _ = ct;
        await _store.MarkDeadLetteredAsync(message.Id, reason, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        _metrics.DeadLettered.Add(1);
        LogDeadLetter(_logger, message.Type, message.Id, message.Attempts + 1, reason);
    }

    private TimeSpan WithJitter(TimeSpan delay)
    {
        if (_options.JitterFactor <= 0) return delay;
#pragma warning disable CA5394 // Jitter does not need a cryptographic random source.
        var spread = (Random.Shared.NextDouble() * 2 - 1) * _options.JitterFactor;
#pragma warning restore CA5394
        return TimeSpan.FromTicks(Math.Max(0, (long)(delay.Ticks * (1 + spread))));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {Type} {MessageId} failed attempt {Attempt}; retrying at {NextAttemptAt}.")]
    private static partial void LogRetry(ILogger logger, string type, Guid messageId, int attempt, DateTimeOffset nextAttemptAt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {Type} {MessageId} dead-lettered after {Attempts} attempt(s): {Reason}")]
    private static partial void LogDeadLetter(ILogger logger, string type, Guid messageId, int attempts, string reason);
}

/// <summary>Background service that keeps calling <see cref="OutboxProcessor.ProcessDueAsync"/>.</summary>
internal sealed partial class OutboxDispatcher(OutboxProcessor processor, OutboxSignal signal, IOptions<OutboxOptions> options, TimeProvider time,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunDispatcher) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wake = signal.WaitAsync();
            int processed;
            try
            {
                processed = await processor.ProcessDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex);
                processed = 0;
            }

            if (processed > 0) continue;
            try
            {
                await Task.WhenAny(wake, Task.Delay(options.Value.PollInterval, time, stoppingToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatcher loop failed; it will retry.")]
    private static partial void LogLoopError(ILogger logger, Exception exception);
}
