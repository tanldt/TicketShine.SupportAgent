using System.Text.RegularExpressions;

namespace TicketShine.Support.Core.Email;

/// <summary>
/// Result of SPF/DKIM/DMARC checks as recorded by the receiving mail server.
/// Only headers stamped by the trusted server (Gmail: "mx.google.com") are used; senders can
/// add their own fake Authentication-Results headers, so those are ignored.
/// </summary>
public sealed record SenderAuthentication(string? Spf, string? Dkim, string? Dmarc, string? DkimDomain, string? DmarcHeaderFrom, bool FoundTrustedHeader)
{
    public static readonly SenderAuthentication None = new(null, null, null, null, null, false);

    public string Summary => FoundTrustedHeader
        ? $"spf={Spf ?? "none"} dkim={Dkim ?? "none"} dmarc={Dmarc ?? "none"}"
        : "no trusted Authentication-Results header";

    /// <summary>
    /// True when DMARC passed for the From domain, or DKIM passed with a signing domain aligned to the From domain.
    /// SPF alone is not enough (it checks the envelope sender, not the visible From address).
    /// </summary>
    public bool IsAuthenticatedFor(string fromAddress)
    {
        if (!FoundTrustedHeader) return false;
        var fromDomain = EmailAddress.Domain(EmailAddress.Normalize(fromAddress));
        if (fromDomain.Length == 0) return false;

        if (Is(Dmarc, "pass") && (DmarcHeaderFrom is null || Aligned(DmarcHeaderFrom, fromDomain))) return true;
        return Is(Dkim, "pass") && DkimDomain is not null && Aligned(DkimDomain, fromDomain);
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Aligned(string signingDomain, string fromDomain)
    {
        var d = signingDomain.Trim().TrimStart('@').ToLowerInvariant();
        return d == fromDomain || fromDomain.EndsWith("." + d, StringComparison.Ordinal) || d.EndsWith("." + fromDomain, StringComparison.Ordinal);
    }
}

public static partial class AuthenticationResultsParser
{
    public const string GmailAuthServId = "mx.google.com";

    [GeneratedRegex(@"^(?<method>spf|dkim|dmarc)\s*=\s*(?<result>[a-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MethodResult();

    [GeneratedRegex(@"header\.(?:i|d)\s*=\s*@?(?<domain>[a-z0-9.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DkimDomain();

    [GeneratedRegex(@"header\.from\s*=\s*(?<domain>[a-z0-9.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFrom();

    public static SenderAuthentication Parse(IEnumerable<string> headers, string trustedAuthServId = GmailAuthServId)
    {
        string? spf = null, dkim = null, dmarc = null, dkimDomain = null, headerFrom = null;
        var trusted = false;

        foreach (var header in headers)
        {
            var segments = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            // The first segment is the authserv-id of the server that stamped the header.
            var servId = segments[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (!string.Equals(servId, trustedAuthServId, StringComparison.OrdinalIgnoreCase)) continue;
            trusted = true;

            foreach (var segment in segments.Skip(1))
            {
                var m = MethodResult().Match(segment);
                if (!m.Success) continue;
                var result = m.Groups["result"].Value.ToLowerInvariant();
                switch (m.Groups["method"].Value.ToLowerInvariant())
                {
                    case "spf":
                        spf ??= result;
                        break;
                    case "dkim":
                        // Several DKIM signatures are possible; keep a passing one if there is one.
                        if (dkim is null || (dkim != "pass" && result == "pass"))
                        {
                            dkim = result;
                            dkimDomain = DkimDomain().Match(segment) is { Success: true } d ? d.Groups["domain"].Value : dkimDomain;
                        }

                        break;
                    case "dmarc":
                        dmarc ??= result;
                        headerFrom ??= HeaderFrom().Match(segment) is { Success: true } h ? h.Groups["domain"].Value : null;
                        break;
                }
            }
        }

        return new SenderAuthentication(spf, dkim, dmarc, dkimDomain, headerFrom, trusted);
    }
}
