using Google.Apis.Gmail.v1.Data;
using TicketShine.Support.Core;
using TicketShine.Support.Core.Domain;

namespace TicketShine.Support.Gmail;

/// <summary>
/// Sends an approved reply from the support mailbox, in the customer's Gmail thread.
/// Idempotent: the Message-ID is derived from the outbox idempotency key, and the sender first checks
/// whether a message with that Message-ID was already sent (for example before a crash).
/// </summary>
public sealed class GmailReplySender(GmailServiceFactory factory, GmailOptions options) : IReplySender
{
    private const string Me = "me";

    public string Name => $"gmail:{options.Mailbox}";

    public bool IsSimulated => false;

    public async Task<SendResult> SendAsync(OutboxMessage outbox, CancellationToken ct = default)
    {
        var gmail = factory.Create();
        var messageId = MimeConversion.MessageIdFor(outbox.IdempotencyKey, options.Mailbox);

        var existing = gmail.Users.Messages.List(Me);
        existing.Q = $"rfc822msgid:{messageId}";
        existing.IncludeSpamTrash = false;
        var found = await existing.ExecuteAsync(ct);
        if (found.Messages is { Count: > 0 } already)
        {
            return SendResult.Ok(already[0].Id);
        }

        var mime = MimeConversion.ToReply(outbox, options.Mailbox, options.SenderDisplayName);
        // threadId + matching "Re:" subject + In-Reply-To/References keep the reply in the customer's thread.
        var sent = await gmail.Users.Messages.Send(new Message { Raw = MimeConversion.ToBase64Url(mime), ThreadId = outbox.ThreadId }, Me).ExecuteAsync(ct);
        return SendResult.Ok(sent.Id);
    }
}
