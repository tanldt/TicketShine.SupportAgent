using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicketShine.Support.Core;
using TicketShine.Support.Core.Domain;

namespace TicketShine.Support.Infrastructure.Sending;

/// <summary>Writes messages to the simulated mailbox by idempotency key. Does not send real email.</summary>
public sealed class SimulatedSender(ICaseRepository repository, TimeProvider time) : IReplySender
{
    public string Name => "simulated-sender";

    public bool IsSimulated => true;

    public async Task<SendResult> SendAsync(OutboxMessage message, CancellationToken ct = default)
    {
        // INSERT OR IGNORE on the idempotency key: a repeated send is a no-op that still counts as delivered.
        await repository.WriteSimulatedMessageAsync(
            new SimulatedMessage(message.IdempotencyKey, message.CaseId, message.Recipient, message.Subject, message.Body, time.GetUtcNow(),
                message.ThreadId, message.InReplyTo),
            ct);
        return SendResult.Ok();
    }
}

/// <summary>
/// Processes the outbox: sends each approved reply through the configured sender, then marks it sent.
/// Safe to run repeatedly; a failed send stays pending and is retried on the next run.
/// </summary>
public sealed class OutboxProcessor(ICaseRepository repository, IReplySender sender, TimeProvider time, ILogger<OutboxProcessor>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var processed = 0;
            foreach (var message in await repository.GetPendingOutboxAsync(ct))
            {
                SendResult result;
                try
                {
                    result = await sender.SendAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = SendResult.Failed(ex.Message);
                }

                if (!result.Delivered)
                {
                    _logger.LogWarning("Sending {Key} via {Sender} failed: {Error}. Will retry.", message.IdempotencyKey, sender.Name, result.Error);
                    await repository.AppendAuditAsync(message.CaseId, "SendFailed", sender.Name, $"Send attempt failed: {result.Error}. Will retry.", ct);
                    continue;
                }

                if (await repository.MarkOutboxSentAsync(message.Id, time.GetUtcNow(), sender.Name, sender.IsSimulated, result.ProviderMessageId, ct))
                {
                    processed++;
                }
            }

            return processed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
