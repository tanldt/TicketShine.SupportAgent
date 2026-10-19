using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Email;

namespace TicketShine.Support.Gmail;

/// <summary>Converts between Gmail raw messages (base64url RFC 5322) and the sample's models.</summary>
public static partial class MimeConversion
{
    public const string ProviderName = "gmail";

    public static InboundEmail ToInboundEmail(string gmailId, string? threadId, long? internalDateMs, string rawBase64Url)
    {
        using var stream = new MemoryStream(FromBase64Url(rawBase64Url));
        var message = MimeMessage.Load(stream);

        var from = message.From.Mailboxes.FirstOrDefault();
        var to = message.To.Mailboxes.FirstOrDefault();
        var text = message.TextBody ?? HtmlToText(message.HtmlBody) ?? string.Empty;

        return new InboundEmail(
            ProviderName,
            gmailId,
            threadId,
            Bracket(message.MessageId),
            Bracket(message.InReplyTo),
            message.References.Count == 0 ? null : string.Join(' ', message.References.Select(Bracket)),
            from?.Address ?? string.Empty,
            from?.Name,
            to?.Address ?? string.Empty,
            message.Subject ?? string.Empty,
            StripQuotedReply(text),
            internalDateMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : message.Date,
            message.Headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList(),
            message.Headers["Auto-Submitted"],
            message.Headers["Precedence"]);
    }

    /// <summary>Builds the reply. The Message-ID is derived from the idempotency key so a retried send can be detected.</summary>
    public static MimeMessage ToReply(OutboxMessage outbox, string fromMailbox, string fromName)
    {
        var message = new MimeMessage
        {
            Subject = outbox.Subject,
            MessageId = MessageIdFor(outbox.IdempotencyKey, fromMailbox),
            Body = new TextPart("plain") { Text = outbox.Body },
        };
        message.From.Add(new MailboxAddress(fromName, fromMailbox));
        message.To.Add(MailboxAddress.Parse(outbox.Recipient));
        if (!string.IsNullOrEmpty(outbox.InReplyTo)) message.InReplyTo = outbox.InReplyTo.Trim('<', '>');
        if (!string.IsNullOrEmpty(outbox.References))
        {
            foreach (var id in outbox.References.Split(' ', StringSplitOptions.RemoveEmptyEntries)) message.References.Add(id.Trim('<', '>'));
        }

        return message;
    }

    public static string MessageIdFor(string idempotencyKey, string fromMailbox)
    {
        var domain = EmailAddress.Domain(fromMailbox);
        return $"{idempotencyKey}@{(domain.Length > 0 ? domain : "ticketshine.support")}";
    }

    public static string ToBase64Url(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>Keeps only the new part of a reply: drops "> quoted" lines and everything after "On ... wrote:".</summary>
    public static string StripQuotedReply(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (WroteLine().IsMatch(line)) break;
            if (line.TrimStart().StartsWith('>')) continue;
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    private static string? HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var noScripts = ScriptOrStyle().Replace(html, " ");
        var withBreaks = BreakTags().Replace(noScripts, "\n");
        return WebUtility.HtmlDecode(Tags().Replace(withBreaks, string.Empty)).Trim();
    }

    private static string? Bracket(string? id) => string.IsNullOrWhiteSpace(id) ? null : $"<{id.Trim().Trim('<', '>')}>";

    [GeneratedRegex(@"^\s*On .{3,200} wrote:\s*$")]
    private static partial Regex WroteLine();

    [GeneratedRegex(@"<(script|style)[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<(br|/p|/div|/li)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
}
