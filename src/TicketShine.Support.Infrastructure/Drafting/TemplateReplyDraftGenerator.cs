using System.Globalization;
using System.Text;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Drafting;

namespace TicketShine.Support.Infrastructure.Drafting;

/// <summary>
/// Demo mode: generates the draft from a deterministic template using the evidence. No LLM call, no AI inference.
/// </summary>
public sealed class TemplateReplyDraftGenerator : IReplyDraftGenerator
{
    public DraftSource Source => DraftSource.Template;

    public string DisplayName => "Template demo — no LLM";

    public Task<DraftGenerationResult> GenerateAsync(DraftContext context, CancellationToken ct)
    {
        var e = context.Evidence;
        if (!e.Order.IsSuccess)
        {
            return Task.FromResult(DraftGenerationResult.Fail("Order evidence is required for a draft."));
        }

        var order = e.Order.Data!;
        var delivery = e.Delivery?.Data;
        var refs = new List<string> { e.Order.SourceId };
        var firstName = order.CustomerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";

        var body = new StringBuilder();
        body.AppendLine($"Hi {firstName},");
        body.AppendLine();
        body.AppendLine($"Thank you for contacting us about order {order.OrderId} for {order.EventName} at {order.Venue} on {Format(order.EventTimeUtc)}.");
        body.AppendLine();

        string subject;
        if (context.Kind == DraftKind.Holding)
        {
            subject = $"We're looking into your order {order.OrderId}";
            body.AppendLine("We're sorry for the wait. Your order is being reviewed by a member of our support team right now.");
            body.AppendLine("We will follow up with an update as soon as we have more information.");
            if (e.Delivery is { IsSuccess: true }) refs.Add(e.Delivery.SourceId);
        }
        else if (context.Classification == Classification.Delivered && delivery is not null)
        {
            subject = $"Your tickets for order {order.OrderId}";
            refs.Add(e.Delivery!.SourceId);
            body.AppendLine($"Our records show your tickets were delivered on {Format(delivery.UpdatedAtUtc)} by {delivery.Method.ToLowerInvariant()} to {delivery.MaskedRecipient}.");
            if (!string.IsNullOrWhiteSpace(delivery.Detail))
            {
                body.AppendLine($"Delivery note: {delivery.Detail}");
            }

            body.AppendLine("If you can't find it, please check your spam or promotions folder as well.");
            AppendPolicy(body, e, refs);
        }
        else if (context.Classification == Classification.InProgress && delivery is not null)
        {
            subject = $"Update on your order {order.OrderId}";
            refs.Add(e.Delivery!.SourceId);
            body.AppendLine($"Your tickets have not been delivered yet. The current delivery status is \"{delivery.Status}\" (last updated {Format(delivery.UpdatedAtUtc)}).");
            body.AppendLine($"The delivery deadline on record for this order is {Format(order.DeliveryDeadlineUtc)}. We will email you as soon as your tickets are delivered.");
            AppendPolicy(body, e, refs);
        }
        else
        {
            return Task.FromResult(DraftGenerationResult.Fail($"No template for classification {context.Classification}."));
        }

        body.AppendLine();
        body.AppendLine("Best regards,");
        body.Append("Ticket Shine Support");

        return Task.FromResult(DraftGenerationResult.Ok(new DraftProposal(subject, body.ToString(), refs)));
    }

    private static void AppendPolicy(StringBuilder body, EvidenceBundle e, List<string> refs)
    {
        if (e.Policy is { IsSuccess: true } policy)
        {
            refs.Add(policy.SourceId);
            body.AppendLine();
            body.AppendLine(policy.Data!.Text);
        }
    }

    private static string Format(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("ddd, MMM d yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
