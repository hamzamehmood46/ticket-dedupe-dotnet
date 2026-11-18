using System.Collections.Concurrent;
using System.Text;

namespace Dedupe.Core;

/// <summary>
/// Finds past tickets that look like a new one and drafts a reply from how they were resolved. It only suggests: it
/// never merges tickets or sends anything, and the draft cites only tickets that were actually retrieved.
/// </summary>
public sealed class DuplicateFinder
{
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly ConcurrentDictionary<string, Ticket> _tickets;

    private DuplicateFinder(IEmbedder embedder, IVectorIndex index, ConcurrentDictionary<string, Ticket> tickets, FinderOptions options)
    {
        _embedder = embedder;
        _index = index;
        _tickets = tickets;
        Options = options;
    }

    public FinderOptions Options { get; }

    /// <summary>A snapshot of the tickets at the moment of the call.</summary>
    public IReadOnlyCollection<Ticket> Tickets => _tickets.Values.ToArray();

    /// <summary>Cleans and embeds every ticket and stores the vectors. The embedder's word weights are fitted on these tickets.</summary>
    public static async Task<DuplicateFinder> CreateAsync(IEnumerable<Ticket> tickets, FinderOptions? options = null, IVectorIndex? index = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? FinderOptions.Default;
        opts.Validate();

        var list = tickets.ToList();
        var byId = new ConcurrentDictionary<string, Ticket>(StringComparer.Ordinal);
        foreach (var t in list)
        {
            if (!byId.TryAdd(t.Id, t))
            {
                throw new ArgumentException($"Ticket id '{t.Id}' appears more than once.", nameof(tickets));
            }
        }

        var embedder = HashingEmbedder.Fit(list.Select(Text));
        var store = index ?? new FlatVectorIndex();
        foreach (var t in list)
        {
            await store.UpsertAsync(ToIndexed(t, embedder.Embed(Text(t))), cancellationToken);
        }

        return new DuplicateFinder(embedder, store, byId, opts);
    }

    /// <summary>
    /// Adds one ticket without refitting the word weights, so existing vectors stay valid. If many new tickets arrive,
    /// fit again with <see cref="CreateAsync"/>.
    /// </summary>
    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        _tickets[ticket.Id] = ticket;
        await _index.UpsertAsync(ToIndexed(ticket, _embedder.Embed(Text(ticket))), cancellationToken);
    }

    public async Task<FindResult> FindAsync(NewTicket ticket, SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var f = filters ?? new SearchFilters();
        if (f.WithinDays is < 1)
        {
            throw new ArgumentException("WithinDays must be at least 1.", nameof(filters));
        }

        var cleaned = TicketCleaner.Clean(ticket.Subject, ticket.Body);
        var size = await _index.CountAsync(cancellationToken);
        var vector = _embedder.Embed(cleaned);
        if (Vectors.IsZero(vector))
        {
            // Nothing the embedder understood (an empty ticket, or only ids and greetings): there is nothing to match on.
            return new FindResult(cleaned, [], null, size);
        }

        var now = ticket.CreatedAt ?? DateTime.UtcNow;
        var filter = new IndexFilter(
            f.Product,
            f.ResolvedOnly,
            f.WithinDays is { } days ? now.AddDays(-days) : null,
            ticket.CreatedAt,           // when the ticket has a date, only older tickets can be its original
            ticket.Id);

        var hits = await _index.SearchAsync(vector, Options.MaxResults, filter, cancellationToken);
        var matches = hits
            .Where(h => _tickets.ContainsKey(h.Id))
            .Select(h => new SimilarTicket(_tickets[h.Id], h.Score, Classify(h.Score)))
            .Where(m => m.Verdict != Verdict.None)
            .ToList();

        return new FindResult(cleaned, matches, BuildDraft(matches), size);
    }

    public Verdict Classify(double score) =>
        score >= Options.DuplicateThreshold ? Verdict.Duplicate
        : score >= Options.RelatedThreshold ? Verdict.Related
        : Verdict.None;

    /// <summary>The text of a ticket as the model sees it. Used for both indexing and evaluation so they cannot drift apart.</summary>
    public static string Text(Ticket t) => TicketCleaner.Clean(t.Subject, t.Body);

    private static IndexedTicket ToIndexed(Ticket t, float[] vector) =>
        new(t.Id, t.Product, t.Status == TicketStatus.Resolved, t.CreatedAt, vector);

    /// <summary>
    /// A starting point for a reply, from the best match only. An open duplicate gets "we are already tracking this";
    /// a resolved one gets how it was fixed, worded as a possibility when the match is only related.
    /// </summary>
    internal static string? BuildDraft(IReadOnlyList<SimilarTicket> matches)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var top = matches[0];
        var sb = new StringBuilder("Hi,\n\nThanks for reporting this.");

        if (top.Verdict == Verdict.Duplicate && top.Ticket.Status == TicketStatus.Open)
        {
            sb.Append($" This matches a ticket we are already working on ({top.Ticket.Id}: \"{top.Ticket.Subject}\"). ")
              .Append("We will update you here as soon as there is news.");
            return sb.ToString();
        }

        var fixedBy = matches.FirstOrDefault(m => m.Ticket.Status == TicketStatus.Resolved && !string.IsNullOrWhiteSpace(m.Ticket.Resolution));
        if (fixedBy is null)
        {
            return null;
        }

        sb.Append(fixedBy.Verdict == Verdict.Duplicate
            ? $" We have seen this before ({fixedBy.Ticket.Id}: \"{fixedBy.Ticket.Subject}\"). What fixed it: {fixedBy.Ticket.Resolution}"
            : $" This may be related to {fixedBy.Ticket.Id} (\"{fixedBy.Ticket.Subject}\"). What fixed that one, which may or may not apply here: {fixedBy.Ticket.Resolution}");
        sb.Append("\n\nIf that does not help, reply here and we will look into it.");
        return sb.ToString();
    }
}
