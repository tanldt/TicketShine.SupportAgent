using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Http;
using MimeKit;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Gmail;
using TicketShine.Support.Infrastructure.Persistence;

namespace TicketShine.Support.Tests;

/// <summary>
/// The Gmail adapter against a fake Gmail REST API (the real Google client library, fake HTTP).
/// This checks request/response handling only; it is not a test against a real Gmail mailbox.
/// </summary>
public sealed class GmailAdapterTests : IAsyncDisposable
{
    private const string Mailbox = "support@ticketshine.example";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "ts-support-tests", $"{Guid.NewGuid():N}.db");
    private readonly GmailOptions _options = new() { Mailbox = Mailbox };

    [Fact]
    public async Task First_poll_starts_at_the_current_history_then_new_messages_are_read_and_labelled()
    {
        var store = await StoreAsync();
        var raw = CustomerMime();
        var gmail = new FakeGmail((req, body) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("GET", var p) when p.EndsWith("/profile") => Json(new JsonObject { ["emailAddress"] = Mailbox, ["historyId"] = "1000" }),
            ("GET", var p) when p.EndsWith("/history") => Json(new JsonObject
            {
                ["history"] = new JsonArray(new JsonObject
                {
                    ["id"] = "1001",
                    ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = "m1", ["threadId"] = "t1" } }),
                }),
                ["historyId"] = "1001",
            }),
            ("GET", var p) when p.EndsWith("/messages/m1") => Json(new JsonObject
            {
                ["id"] = "m1", ["threadId"] = "t1", ["labelIds"] = new JsonArray("INBOX"), ["internalDate"] = "1792260000000", ["raw"] = raw,
            }),
            ("GET", var p) when p.EndsWith("/labels") => Json(new JsonObject { ["labels"] = new JsonArray() }),
            ("POST", var p) when p.EndsWith("/labels") => Json(new JsonObject { ["id"] = "Label_7", ["name"] = "TS-Agent/Ingested" }),
            ("POST", var p) when p.EndsWith("/messages/m1/modify") => Json(new JsonObject { ["id"] = "m1" }),
            _ => NotFound(),
        });
        var source = new GmailInboundSource(new GmailServiceFactory(_options, gmail), _options, store);

        Assert.Empty(await source.FetchNewAsync(default));
        Assert.Equal("1000", await store.GetSyncStateAsync($"gmail:historyId:{Mailbox}"));

        var email = Assert.Single(await source.FetchNewAsync(default));
        Assert.Equal("gmail", email.Provider);
        Assert.Equal("m1", email.ProviderMessageId);
        Assert.Equal("t1", email.ThreadId);
        Assert.Equal("jamie.rivera@example.com", email.FromAddress);
        Assert.Equal("<abc123@mail.example.com>", email.RfcMessageId);
        Assert.Equal("Will my tickets for TS-10025 arrive?", email.TextBody); // quoted part removed
        Assert.Equal(2, email.AuthenticationResults.Count);
        Assert.Equal("1001", await store.GetSyncStateAsync($"gmail:historyId:{Mailbox}"));

        await source.MarkProcessedAsync(email, "CaseCreated", default);
        var modify = gmail.Requests.Single(r => r.Path.EndsWith("/modify"));
        Assert.Contains("Label_7", modify.Body);
    }

    [Fact]
    public async Task Expired_history_triggers_a_resync_instead_of_losing_mail()
    {
        var store = await StoreAsync();
        await store.SetSyncStateAsync($"gmail:historyId:{Mailbox}", "5");
        var gmail = new FakeGmail((req, _) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("GET", var p) when p.EndsWith("/history") => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"code":404,"message":"Requested entity was not found.","errors":[{"reason":"notFound"}]}}""", Encoding.UTF8, "application/json"),
            },
            ("GET", var p) when p.EndsWith("/messages") => Json(new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["id"] = "m2", ["threadId"] = "t2" }) }),
            ("GET", var p) when p.EndsWith("/profile") => Json(new JsonObject { ["emailAddress"] = Mailbox, ["historyId"] = "2000" }),
            ("GET", var p) when p.EndsWith("/messages/m2") => Json(new JsonObject { ["id"] = "m2", ["threadId"] = "t2", ["raw"] = CustomerMime() }),
            _ => NotFound(),
        });
        var source = new GmailInboundSource(new GmailServiceFactory(_options, gmail), _options, store);

        var email = Assert.Single(await source.FetchNewAsync(default));

        Assert.Equal("m2", email.ProviderMessageId);
        Assert.Contains(gmail.Requests, r => r.Query.Contains("newer_than"));
        Assert.Equal("2000", await store.GetSyncStateAsync($"gmail:historyId:{Mailbox}"));
    }

    [Fact]
    public async Task Reply_is_sent_in_the_customer_thread_and_a_retry_does_not_send_twice()
    {
        var sentIds = new List<string>();
        var gmail = new FakeGmail((req, body) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("GET", var p) when p.EndsWith("/messages") => Json(sentIds.Count == 0
                ? new JsonObject()
                : new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["id"] = sentIds[0], ["threadId"] = "t1" }) }),
            ("POST", var p) when p.EndsWith("/messages/send") => SendOk(sentIds),
            _ => NotFound(),
        });
        var sender = new GmailReplySender(new GmailServiceFactory(_options, gmail), _options);
        var outbox = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "case-abc-v1", "jamie.rivera@example.com",
            "Re: Tickets for tomorrow??", "Hi Jamie,\n\nYour tickets are on the way.\n\nTicket Shine Support", OutboxStatuses.Pending,
            DateTimeOffset.UtcNow, null, ThreadId: "t1", InReplyTo: "<abc123@mail.example.com>", References: "<abc123@mail.example.com>");

        var first = await sender.SendAsync(outbox);
        var retry = await sender.SendAsync(outbox);

        Assert.True(first.Delivered);
        Assert.Equal("s1", first.ProviderMessageId);
        Assert.Equal("s1", retry.ProviderMessageId);
        var send = Assert.Single(gmail.Requests, r => r.Path.EndsWith("/send"));
        var json = JsonNode.Parse(send.Body)!;
        Assert.Equal("t1", json["threadId"]!.GetValue<string>());

        var mime = MimeMessage.Load(new MemoryStream(MimeConversion.FromBase64Url(json["raw"]!.GetValue<string>())));
        Assert.Equal("Re: Tickets for tomorrow??", mime.Subject);
        Assert.Equal("abc123@mail.example.com", mime.InReplyTo);
        Assert.Contains("abc123@mail.example.com", mime.References);
        Assert.Equal("case-abc-v1@ticketshine.example", mime.MessageId);
        Assert.Equal("jamie.rivera@example.com", mime.To.Mailboxes.Single().Address);
        Assert.Equal(Mailbox, mime.From.Mailboxes.Single().Address);
        Assert.Contains(gmail.Requests, r => r.Query.Contains("rfc822msgid"));
    }

    [Fact]
    public void Missing_gmail_configuration_fails_clearly()
    {
        var factory = new GmailServiceFactory(new GmailOptions { Mailbox = Mailbox });
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Contains("ServiceAccountKeyPath", ex.Message);
    }

    private async Task<SqliteCaseRepository> StoreAsync()
    {
        var repo = new SqliteCaseRepository(_dbPath);
        await repo.InitializeAsync();
        return repo;
    }

    private static string CustomerMime()
    {
        var m = new MimeMessage { Subject = "Tickets for tomorrow??", MessageId = "abc123@mail.example.com" };
        m.From.Add(new MailboxAddress("Jamie Rivera", "jamie.rivera@example.com"));
        m.To.Add(MailboxAddress.Parse(Mailbox));
        m.Headers.Add("Authentication-Results", "mx.google.com; dkim=pass header.i=@example.com; spf=pass smtp.mailfrom=jamie.rivera@example.com; dmarc=pass header.from=example.com");
        m.Headers.Add("Authentication-Results", "evil.example; dmarc=pass header.from=example.com");
        m.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "Will my tickets for TS-10025 arrive?\n\nOn Fri, Oct 16, 2026 at 9:00 AM Ticket Shine wrote:\n> Thanks for your order." },
            new TextPart("html") { Text = "<p>Will my tickets for TS-10025 arrive?</p>" },
        };
        return MimeConversion.ToBase64Url(m);
    }

    private static HttpResponseMessage SendOk(List<string> sentIds)
    {
        sentIds.Add("s1");
        return Json(new JsonObject { ["id"] = "s1", ["threadId"] = "t1" });
    }

    private static HttpResponseMessage Json(JsonObject o) => new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent("""{"error":{"code":404,"message":"unexpected request in test"}}""", Encoding.UTF8, "application/json"),
    };

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private sealed record SeenRequest(string Method, string Path, string Query, string Body);

    /// <summary>Plugs a fake HTTP handler into the Google client library.</summary>
    private sealed class FakeGmail(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpClientFactory
    {
        public List<SeenRequest> Requests { get; } = [];

        private Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; } = respond;

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new Handler(this);

        private sealed class Handler(FakeGmail owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = string.Empty;
                if (request.Content is not null)
                {
                    // The Google client gzips request bodies.
                    var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                    {
                        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                        using var reader = new StreamReader(gz);
                        body = await reader.ReadToEndAsync(cancellationToken);
                    }
                    else
                    {
                        body = Encoding.UTF8.GetString(bytes);
                    }
                }
                lock (owner.Requests)
                {
                    owner.Requests.Add(new SeenRequest(request.Method.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query), body));
                }

                return owner.Respond(request, body);
            }
        }
    }
}
