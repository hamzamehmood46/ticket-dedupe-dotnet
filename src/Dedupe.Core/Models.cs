namespace Dedupe.Core;

public enum TicketStatus { Open, Resolved }

/// <summary>
/// A support ticket. <see cref="GroupId"/> is the ground truth used only for evaluation: tickets that share a non-null
/// group are true duplicates of each other. Real tickets do not have one.
/// </summary>
public sealed record Ticket(
    string Id,
    string Product,
    string Subject,
    string Body,
    DateTime CreatedAt,
    TicketStatus Status,
    string? Resolution = null,
    string? GroupId = null);

/// <summary>A ticket that has just come in and is not in the index yet.</summary>
public sealed record NewTicket(string Subject, string Body, DateTime? CreatedAt = null, string? Id = null);

/// <summary>Limits on which past tickets can match. Everything is optional.</summary>
public sealed record SearchFilters(string? Product = null, bool ResolvedOnly = false, int? WithinDays = null);

public enum Verdict { None, Related, Duplicate }

public sealed record SimilarTicket(Ticket Ticket, double Score, Verdict Verdict);

/// <param name="CleanedText">What the model actually saw after quotes, signatures and ids were removed.</param>
/// <param name="DraftReply">A reply an agent could edit, built only from the tickets in <paramref name="Matches"/>. Null when there is nothing to base one on.</param>
public sealed record FindResult(string CleanedText, IReadOnlyList<SimilarTicket> Matches, string? DraftReply, int IndexSize);

/// <param name="DuplicateThreshold">At or above this cosine score a past ticket is called a likely duplicate.</param>
/// <param name="RelatedThreshold">At or above this (and below the duplicate threshold) it is called related.</param>
public sealed record FinderOptions(double DuplicateThreshold, double RelatedThreshold, int MaxResults = 5)
{
    /// <summary>The thresholds Evaluator chose on the development half of the sample tickets. A test keeps these in step with the evaluation.</summary>
    public static FinderOptions Default { get; } = new(0.28, 0.13);

    public void Validate()
    {
        if (double.IsNaN(DuplicateThreshold) || double.IsNaN(RelatedThreshold) || RelatedThreshold < 0 || DuplicateThreshold > 1 || RelatedThreshold > DuplicateThreshold)
        {
            throw new ArgumentException("Thresholds must satisfy 0 <= RelatedThreshold <= DuplicateThreshold <= 1.");
        }

        if (MaxResults < 1 || MaxResults > 50)
        {
            throw new ArgumentException("MaxResults must be from 1 to 50.");
        }
    }
}
