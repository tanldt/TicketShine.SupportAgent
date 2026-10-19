using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace TicketShine.Support.Gmail;

public sealed class GmailOptions
{
    public const string Section = "Mail:Gmail";

    /// <summary>The support mailbox to read and send from, for example support@ticketshine.com.</summary>
    public string Mailbox { get; set; } = string.Empty;

    /// <summary>Display name used on replies.</summary>
    public string SenderDisplayName { get; set; } = "Ticket Shine Support";

    /// <summary>
    /// Path to a Google service account JSON key with domain-wide delegation for <see cref="Mailbox"/>.
    /// Keep it outside the repository (user secrets, a mounted secret, or a key vault).
    /// </summary>
    public string? ServiceAccountKeyPath { get; set; }

    /// <summary>Label added to messages that were turned into a case, so staff can see it in Gmail.</summary>
    public string ProcessedLabel { get; set; } = "TS-Agent/Ingested";

    public int MaxMessagesPerPoll { get; set; } = 25;

    /// <summary>Query used when the stored history ID is too old and a resync is needed.</summary>
    public string ResyncQuery { get; set; } = "in:inbox newer_than:2d";

    public string ApplicationName { get; set; } = "TicketShine.SupportAgent";
}

/// <summary>Creates authenticated Gmail API clients. Tests pass a custom HTTP factory and no credential.</summary>
public sealed class GmailServiceFactory(GmailOptions options, IHttpClientFactory? httpClientFactory = null, IConfigurableHttpClientInitializer? credentialOverride = null)
{
    /// <summary>Least privilege: read + label (modify) and send. Not the full https://mail.google.com/ scope.</summary>
    public static readonly string[] Scopes = [GmailService.Scope.GmailModify, GmailService.Scope.GmailSend];

    private GmailService? _service;

    public GmailService Create()
    {
        if (_service is not null) return _service;

        var initializer = new BaseClientService.Initializer
        {
            ApplicationName = options.ApplicationName,
            HttpClientInitializer = credentialOverride ?? CreateCredential(),
        };
        if (httpClientFactory is not null) initializer.HttpClientFactory = httpClientFactory;

        return _service = new GmailService(initializer);
    }

    private IConfigurableHttpClientInitializer? CreateCredential()
    {
        if (httpClientFactory is not null && string.IsNullOrWhiteSpace(options.ServiceAccountKeyPath))
        {
            return null; // test mode
        }

        if (string.IsNullOrWhiteSpace(options.ServiceAccountKeyPath) || !File.Exists(options.ServiceAccountKeyPath))
        {
            throw new InvalidOperationException("Gmail is not configured: set Mail:Gmail:ServiceAccountKeyPath to a service account key with domain-wide delegation.");
        }

        if (string.IsNullOrWhiteSpace(options.Mailbox))
        {
            throw new InvalidOperationException("Gmail is not configured: set Mail:Gmail:Mailbox.");
        }

        // Service account impersonating the support mailbox (domain-wide delegation granted by a Workspace admin).
        return CredentialFactory.FromFile<ServiceAccountCredential>(options.ServiceAccountKeyPath)
            .ToGoogleCredential()
            .CreateScoped(Scopes)
            .CreateWithUser(options.Mailbox);
    }
}
