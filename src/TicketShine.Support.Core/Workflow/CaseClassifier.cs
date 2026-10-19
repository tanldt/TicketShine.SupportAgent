using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Drafting;

namespace TicketShine.Support.Core.Workflow;

public sealed record ClassificationDecision(Classification Classification, CaseState NextState, string Reason);

/// <summary>Deterministic classification based on evidence and DemoClock.</summary>
public static class CaseClassifier
{
    public static ClassificationDecision Classify(EvidenceBundle evidence, DateTimeOffset now)
    {
        var order = evidence.Order;
        if (order.Status == LookupStatus.NotFound)
        {
            return new(Classification.NotFound, CaseState.NeedsHuman,
                "Order not found. No recipient on record — verify the order number with the customer through a trusted channel.");
        }

        if (!order.IsSuccess)
        {
            return new(Classification.Unknown, CaseState.LookupFailed, $"Order lookup failed: {order.Error}");
        }

        if (evidence.Delivery is not { IsSuccess: true } delivery)
        {
            var error = evidence.Delivery?.Status == LookupStatus.NotFound
                ? "No delivery record found for this order."
                : evidence.Delivery?.Error ?? "Delivery lookup did not run.";
            return new(Classification.Unknown, CaseState.LookupFailed, $"Delivery lookup failed: {error}");
        }

        if (evidence.Notes is { Status: LookupStatus.Error } notes)
        {
            return new(Classification.Unknown, CaseState.LookupFailed, $"Case notes lookup failed: {notes.Error}");
        }

        if (string.Equals(delivery.Data!.Status, DeliveryStatuses.Delivered, StringComparison.OrdinalIgnoreCase))
        {
            return new(Classification.Delivered, CaseState.AwaitingReview, "Tickets delivered according to the delivery record.");
        }

        if (now > order.Data!.DeliveryDeadlineUtc)
        {
            return new(Classification.Overdue, CaseState.NeedsHuman,
                $"Past delivery deadline ({order.Data.DeliveryDeadlineUtc:u}) and tickets not delivered. A staff member must handle this case; do not promise delivery or a refund.");
        }

        return new(Classification.InProgress, CaseState.AwaitingReview, "Tickets not delivered yet; still before the delivery deadline.");
    }

    public static string PolicyTopicFor(DeliveryInfo? delivery) =>
        delivery is not null && string.Equals(delivery.Status, DeliveryStatuses.Delivered, StringComparison.OrdinalIgnoreCase)
            ? "delivered-tickets"
            : "delivery-timing";
}
