using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicketShine.Support.Core.Email;

namespace TicketShine.Support.Gmail;

/// <summary>
/// Reads new messages from the support mailbox with the Gmail history API.
/// Polling history.list is the simplest reliable start; users.watch + Pub/Sub can later trigger the same code
/// instead of a timer, without changing anything downstream.
/// </summary>
public sealed class GmailInboundSource(GmailServiceFactory factory, GmailOptions options, IMailStore store, ILogger<GmailInboundSource>? logger = null)
    : IInboundMailSource
{
    private const string Me = "me";
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private string? _labelId;

    public string Provider => MimeConversion.ProviderName;

    private string HistoryKey => $"gmail:historyId:{options.Mailbox.ToLowerInvariant()}";

    public async Task<IReadOnlyList<InboundEmail>> FetchNewAsync(CancellationToken ct)
    {
        var gmail = factory.Create();
        var last = await store.GetSyncStateAsync(HistoryKey, ct);

        if (last is null || !ulong.TryParse(last, out var startHistoryId))
        {
            // First run: start from "now" rather than importing the whole mailbox.
            var profile = await gmail.Users.GetProfile(Me).ExecuteAsync(ct);
            await store.SetSyncStateAsync(HistoryKey, profile.HistoryId.ToString()!, ct);
            _logger.LogInformation("Gmail sync initialised at history {HistoryId}", profile.HistoryId);
            return [];
        }

        var (ids, newestHistoryId) = await ListNewMessageIdsAsync(gmail, startHistoryId, ct);

        var emails = new List<InboundEmail>();
        foreach (var id in ids.Take(options.MaxMessagesPerPoll))
        {
            var request = gmail.Users.Messages.Get(Me, id);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
            var message = await request.ExecuteAsync(ct);
            if (message.LabelIds?.Any(l => l is "SENT" or "DRAFT" or "SPAM" or "TRASH") == true) continue;
            emails.Add(MimeConversion.ToInboundEmail(message.Id, message.ThreadId, message.InternalDate, message.Raw));
        }

        // Advance only after every message was fetched; de-duplication covers any overlap on the next poll.
        if (ids.Count <= options.MaxMessagesPerPoll && newestHistoryId is { } newest)
        {
            await store.SetSyncStateAsync(HistoryKey, newest.ToString(), ct);
        }

        return emails;
    }

    public async Task MarkProcessedAsync(InboundEmail email, string outcome, CancellationToken ct)
    {
        if (email.Provider != Provider) return;
        var gmail = factory.Create();
        _labelId ??= await EnsureLabelAsync(gmail, ct);
        await gmail.Users.Messages.Modify(new ModifyMessageRequest { AddLabelIds = [_labelId] }, Me, email.ProviderMessageId).ExecuteAsync(ct);
    }

    private async Task<(List<string> Ids, ulong? NewestHistoryId)> ListNewMessageIdsAsync(GmailService gmail, ulong startHistoryId, CancellationToken ct)
    {
        var ids = new List<string>();
        try
        {
            string? pageToken = null;
            ulong? newest = null;
            do
            {
                var request = gmail.Users.History.List(Me);
                request.StartHistoryId = startHistoryId;
                request.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
                request.LabelId = "INBOX";
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(ct);
                foreach (var added in response.History?.SelectMany(h => h.MessagesAdded ?? []) ?? [])
                {
                    if (added.Message?.Id is { } id && !ids.Contains(id)) ids.Add(id);
                }

                newest = response.HistoryId ?? newest;
                pageToken = response.NextPageToken;
            }
            while (pageToken is not null);

            return (ids, newest);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The stored history ID is too old. Resync recent inbox mail; already-processed messages are de-duplicated.
            _logger.LogWarning("Gmail history {HistoryId} expired; resyncing with '{Query}'", startHistoryId, options.ResyncQuery);
            var list = gmail.Users.Messages.List(Me);
            list.Q = options.ResyncQuery;
            list.MaxResults = options.MaxMessagesPerPoll;
            var response = await list.ExecuteAsync(ct);
            ids.AddRange(response.Messages?.Select(m => m.Id) ?? []);
            var profile = await gmail.Users.GetProfile(Me).ExecuteAsync(ct);
            return (ids, profile.HistoryId);
        }
    }

    private async Task<string> EnsureLabelAsync(GmailService gmail, CancellationToken ct)
    {
        var labels = await gmail.Users.Labels.List(Me).ExecuteAsync(ct);
        var existing = labels.Labels?.FirstOrDefault(l => string.Equals(l.Name, options.ProcessedLabel, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        var created = await gmail.Users.Labels.Create(
            new Label { Name = options.ProcessedLabel, LabelListVisibility = "labelShow", MessageListVisibility = "show" }, Me).ExecuteAsync(ct);
        return created.Id;
    }
}
