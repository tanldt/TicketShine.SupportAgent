using System.Text.RegularExpressions;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Drafting;

namespace TicketShine.Support.Core.Workflow;

public sealed record DraftValidation(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// Validates a draft before saving: order binding, evidence references, length.
/// Escalation warnings do not block saving but are shown to the reviewer.
/// </summary>
public static partial class DraftValidator
{
    public const int MaxSubjectLength = 200;
    public const int MinBodyLength = 20;
    public const int MaxBodyLength = 4000;

    private static readonly string[] CommitmentPhrases =
    [
        "refund", "reimburse", "compensat", "guarantee", "we promise", "will arrive by", "will be delivered by", "cancel your order",
    ];

    private static readonly string[] InstructionLikeNotePhrases =
    [
        "ignore", "send now", "approve", "bypass", "skip review", "system prompt",
    ];

    [GeneratedRegex(@"\bTS-\d{3,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderIdPattern();

    public static DraftValidation Validate(
        string orderId,
        DraftProposal proposal,
        IReadOnlyCollection<string> availableSourceIds,
        Classification classification,
        CaseNotesInfo? notes,
        bool requireEvidence = true)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(proposal.Subject) || proposal.Subject.Length > MaxSubjectLength)
        {
            errors.Add($"Subject must be 1–{MaxSubjectLength} characters.");
        }

        var bodyLength = proposal.Body?.Trim().Length ?? 0;
        if (bodyLength < MinBodyLength || bodyLength > MaxBodyLength)
        {
            errors.Add($"Body must be {MinBodyLength}–{MaxBodyLength} characters (got {bodyLength}).");
        }

        var text = $"{proposal.Subject}\n{proposal.Body}";
        foreach (Match m in OrderIdPattern().Matches(text))
        {
            if (!string.Equals(m.Value, orderId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Draft references another order ({m.Value}); it must only discuss {orderId}.");
            }
        }

        if (requireEvidence && proposal.EvidenceRefs.Count == 0)
        {
            errors.Add("Draft must cite at least one evidence source.");
        }

        foreach (var reference in proposal.EvidenceRefs)
        {
            if (!availableSourceIds.Contains(reference))
            {
                errors.Add($"Unknown evidence reference '{reference}'.");
            }
        }

        var lower = text.ToLowerInvariant();
        if (CommitmentPhrases.Any(lower.Contains))
        {
            warnings.Add("Draft mentions a refund, compensation, guarantee or delivery commitment. Confirm with policy and a supervisor before approving.");
        }

        if (classification == Classification.Overdue)
        {
            warnings.Add("Order is past its delivery deadline — escalation required. Do not promise delivery or a refund.");
        }

        if (notes?.Notes.Any(n => InstructionLikeNotePhrases.Any(p => n.Text.Contains(p, StringComparison.OrdinalIgnoreCase))) == true)
        {
            warnings.Add("Case notes contain instruction-like text. It is treated as data only and cannot change the workflow.");
        }

        return new(errors.Count == 0, errors.Distinct().ToList(), warnings);
    }
}
