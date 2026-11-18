namespace Dedupe.Core;

/// <summary>What the index stores about a ticket: enough to filter on, plus the vector. The ticket text itself lives elsewhere.</summary>
public sealed record IndexedTicket(string Id, string Product, bool Resolved, DateTime CreatedAt, float[] Vector);

public sealed record VectorHit(string Id, double Score);

/// <summary>Which stored tickets a search may consider. Every filter is applied before ranking, not after.</summary>
public sealed record IndexFilter(
    string? Product = null,
    bool ResolvedOnly = false,
    DateTime? CreatedAfter = null,
    DateTime? CreatedBefore = null,
    string? ExcludeId = null);

/// <summary>
/// A vector store with metadata filters. <see cref="FlatVectorIndex"/> keeps everything in memory;
/// the Dedupe.PgVector project has a Postgres/pgvector version of the same contract.
/// </summary>
public interface IVectorIndex
{
    Task UpsertAsync(IndexedTicket item, CancellationToken cancellationToken = default);

    /// <summary>The <paramref name="k"/> most similar stored tickets that pass the filter, best first. Ties are broken by id so results are repeatable.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, int k, IndexFilter filter, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Exact search: every stored vector that passes the filter is compared with the query. That is the right choice up to
/// tens of thousands of tickets, and it is the baseline an approximate index would be measured against.
/// </summary>
public sealed class FlatVectorIndex : IVectorIndex
{
    private readonly Dictionary<string, IndexedTicket> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _dimensions;

    public Task UpsertAsync(IndexedTicket item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("A ticket needs an id.", nameof(item));
        }

        if (!Vectors.IsFinite(item.Vector))
        {
            throw new ArgumentException("The vector is empty or contains NaN or infinity.", nameof(item));
        }

        lock (_gate)
        {
            if (_dimensions == 0)
            {
                _dimensions = item.Vector.Length;
            }
            else if (item.Vector.Length != _dimensions)
            {
                throw new ArgumentException($"The index holds {_dimensions}-dimension vectors and this one has {item.Vector.Length}.", nameof(item));
            }

            _items[item.Id] = item with { Vector = (float[])item.Vector.Clone() };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, int k, IndexFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        if (!Vectors.IsFinite(query))
        {
            throw new ArgumentException("The query vector is empty or contains NaN or infinity.", nameof(query));
        }

        IndexedTicket[] snapshot;
        lock (_gate)
        {
            if (_dimensions != 0 && query.Length != _dimensions)
            {
                throw new ArgumentException($"The index holds {_dimensions}-dimension vectors and the query has {query.Length}.", nameof(query));
            }

            snapshot = _items.Values.ToArray();
        }

        if (k <= 0 || Vectors.IsZero(query))
        {
            return Task.FromResult<IReadOnlyList<VectorHit>>([]);
        }

        IReadOnlyList<VectorHit> hits = snapshot
            .Where(t => Passes(t, filter))
            .Select(t => new VectorHit(t.Id, Vectors.Cosine(query, t.Vector)))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .Take(k)
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Remove(id));
        }
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Count);
        }
    }

    private static bool Passes(IndexedTicket t, IndexFilter f) =>
        (f.Product is null || string.Equals(t.Product, f.Product, StringComparison.Ordinal))
        && (!f.ResolvedOnly || t.Resolved)
        && (f.CreatedAfter is null || t.CreatedAt >= f.CreatedAfter)
        && (f.CreatedBefore is null || t.CreatedAt < f.CreatedBefore)
        && (f.ExcludeId is null || !string.Equals(t.Id, f.ExcludeId, StringComparison.Ordinal));
}
