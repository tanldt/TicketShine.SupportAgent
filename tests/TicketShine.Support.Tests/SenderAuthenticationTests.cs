using TicketShine.Support.Core.Email;

namespace TicketShine.Support.Tests;

public sealed class SenderAuthenticationTests
{
    private const string GmailPass =
        "mx.google.com; dkim=pass header.i=@example.com header.s=s1 header.b=abc; spf=pass (google.com: domain of jamie@example.com designates 192.0.2.1 as permitted sender) smtp.mailfrom=jamie@example.com; dmarc=pass (p=REJECT sp=REJECT dis=NONE) header.from=example.com";

    [Fact]
    public void Gmail_pass_header_authenticates_the_from_domain()
    {
        var auth = AuthenticationResultsParser.Parse([GmailPass]);

        Assert.True(auth.FoundTrustedHeader);
        Assert.Equal("pass", auth.Dmarc);
        Assert.True(auth.IsAuthenticatedFor("jamie.rivera@example.com"));
        Assert.Equal("spf=pass dkim=pass dmarc=pass", auth.Summary);
    }

    [Fact]
    public void Dmarc_for_another_domain_does_not_authenticate()
    {
        var auth = AuthenticationResultsParser.Parse([GmailPass]);
        Assert.False(auth.IsAuthenticatedFor("someone@example.org"));
    }

    [Fact]
    public void Dmarc_fail_is_not_authenticated()
    {
        var auth = AuthenticationResultsParser.Parse(["mx.google.com; dkim=fail header.i=@example.com; spf=softfail smtp.mailfrom=x@example.com; dmarc=fail (p=QUARANTINE) header.from=example.com"]);
        Assert.False(auth.IsAuthenticatedFor("alex.chen@example.com"));
    }

    [Fact]
    public void Headers_from_other_servers_are_ignored()
    {
        var forged = "mail.attacker.example; dkim=pass header.i=@example.com; spf=pass; dmarc=pass header.from=example.com";
        var auth = AuthenticationResultsParser.Parse([forged]);

        Assert.False(auth.FoundTrustedHeader);
        Assert.False(auth.IsAuthenticatedFor("jamie.rivera@example.com"));
    }

    [Fact]
    public void Forged_header_cannot_override_the_trusted_result()
    {
        var forged = "mx.attacker.example; dmarc=pass header.from=example.com";
        var real = "mx.google.com; dkim=none; spf=fail smtp.mailfrom=x@example.com; dmarc=fail header.from=example.com";

        Assert.False(AuthenticationResultsParser.Parse([forged, real]).IsAuthenticatedFor("jamie.rivera@example.com"));
    }

    [Fact]
    public void Aligned_dkim_without_dmarc_authenticates()
    {
        var auth = AuthenticationResultsParser.Parse(["mx.google.com; dkim=pass header.i=@mail.example.com; spf=neutral"]);
        Assert.True(auth.IsAuthenticatedFor("jamie.rivera@example.com"));
    }

    [Fact]
    public void Spf_alone_is_not_enough()
    {
        var auth = AuthenticationResultsParser.Parse(["mx.google.com; spf=pass smtp.mailfrom=bounce@example.com"]);
        Assert.False(auth.IsAuthenticatedFor("jamie.rivera@example.com"));
    }

    [Fact]
    public void Order_ids_are_extracted_once_and_upper_cased()
    {
        Assert.Equal(["TS-10025", "TS-10026"], OrderIdExtractor.FindAll("Re: ts-10025", "about TS-10025 and TS-10026"));
        Assert.Empty(OrderIdExtractor.FindAll("no order here", null));
    }
}
