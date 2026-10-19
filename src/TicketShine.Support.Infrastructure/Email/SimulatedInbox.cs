using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicketShine.Support.Core.Email;

namespace TicketShine.Support.Infrastructure.Email;

/// <summary>
/// In-memory stand-in for the support Gmail inbox. Messages look like what the Gmail adapter produces
/// (message ID, thread ID, RFC Message-ID, Authentication-Results stamped by mx.google.com).
/// </summary>
public sealed class SimulatedInboundMailSource : IInboundMailSource
{
    public const string ProviderName = "simulated-gmail";

    private readonly ConcurrentQueue<InboundEmail> _queue = new();

    public string Provider => ProviderName;

    public void Deliver(InboundEmail email) => _queue.Enqueue(email);

    public Task<IReadOnlyList<InboundEmail>> FetchNewAsync(CancellationToken ct)
    {
        var batch = new List<InboundEmail>();
        while (batch.Count < 50 && _queue.TryDequeue(out var email)) batch.Add(email);
        return Task.FromResult<IReadOnlyList<InboundEmail>>(batch);
    }

    public Task MarkProcessedAsync(InboundEmail email, string outcome, CancellationToken ct) => Task.CompletedTask;
}

public enum SimulatedAuth
{
    /// <summary>DKIM, SPF and DMARC pass for the From domain.</summary>
    Pass,

    /// <summary>DMARC fails: the From address is likely spoofed.</summary>
    Fail,

    /// <summary>No Authentication-Results from the trusted server, only a forged one added by the sender.</summary>
    ForgedHeaderOnly,
}

/// <summary>Builds realistic simulated Gmail messages.</summary>
public static class SimulatedEmailFactory
{
    public static InboundEmail Create(
        string fromAddress,
        string? fromName,
        string subject,
        string body,
        SimulatedAuth auth,
        string supportMailbox,
        string? threadId = null,
        string? inReplyTo = null,
        DateTimeOffset? receivedAt = null,
        string? autoSubmitted = null)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var domain = EmailAddress.Domain(fromAddress);
        var rfcId = $"<{id}@mail.{(domain.Length > 0 ? domain : "example.com")}>";
        IReadOnlyList<string> headers = auth switch
        {
            SimulatedAuth.Pass =>
            [
                $"mx.google.com; dkim=pass header.i=@{domain} header.s=s1; spf=pass (google.com: domain of {fromAddress} designates 192.0.2.10 as permitted sender) smtp.mailfrom={fromAddress}; dmarc=pass (p=REJECT sp=REJECT dis=NONE) header.from={domain}",
            ],
            SimulatedAuth.Fail =>
            [
                $"mx.google.com; dkim=fail header.i=@{domain}; spf=softfail (google.com: domain of transitioning {fromAddress} does not designate 203.0.113.66 as permitted sender) smtp.mailfrom={fromAddress}; dmarc=fail (p=QUARANTINE dis=QUARANTINE) header.from={domain}",
            ],
            _ =>
            [
                // Added by the sender, not by Gmail. Must be ignored.
                $"mail.attacker.example; dkim=pass header.i=@{domain}; spf=pass smtp.mailfrom={fromAddress}; dmarc=pass header.from={domain}",
            ],
        };

        return new InboundEmail(
            SimulatedInboundMailSource.ProviderName,
            ProviderMessageId: id,
            ThreadId: threadId ?? $"thread-{id}",
            RfcMessageId: rfcId,
            InReplyTo: inReplyTo,
            References: inReplyTo,
            FromAddress: fromAddress,
            FromName: fromName,
            ToAddress: supportMailbox,
            Subject: subject,
            TextBody: body,
            ReceivedAtUtc: receivedAt ?? DateTimeOffset.UtcNow,
            AuthenticationResults: headers,
            AutoSubmitted: autoSubmitted);
    }
}

/// <summary>One polling pass: fetch new mail, turn each message into a case (or append it), tell the provider.</summary>
public sealed class MailIngestProcessor(IInboundMailSource source, EmailIngestService ingest, ILogger<MailIngestProcessor>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public string Provider => source.Provider;

    public async Task<IReadOnlyList<IngestResult>> RunOnceAsync(CancellationToken ct)
    {
        var results = new List<IngestResult>();
        foreach (var email in await source.FetchNewAsync(ct))
        {
            var result = await ingest.IngestAsync(email, ct);
            results.Add(result);
            _logger.LogInformation("Inbound {Provider}/{Id}: {Outcome} {CaseId}", email.Provider, email.ProviderMessageId, result.Outcome, result.CaseId);

            if (result.Outcome != IngestOutcomes.Failed)
            {
                await source.MarkProcessedAsync(email, result.Outcome, ct);
            }
        }

        return results;
    }
}
