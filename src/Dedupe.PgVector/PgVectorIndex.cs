using System.Globalization;
using System.Text.RegularExpressions;
using Dedupe.Core;

namespace Dedupe.PgVector;

/// <summary>The only thing <see cref="PgVectorIndex"/> needs from a database. A fake of this makes the SQL testable without Postgres.</summary>
public interface ISqlRunner
{
    /// <summary>Runs a statement. Parameter names have no "@" here; the SQL refers to them as @name.</summary>
    Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The <see cref="IVectorIndex"/> contract on Postgres with the pgvector extension. Every value reaches the database as
/// a parameter; only the table name and the vector size are written into SQL, and both are checked first.
/// Note: this has been tested against a fake <see cref="ISqlRunner"/> that records the SQL, not against a live Postgres.
/// </summary>
public sealed class PgVectorIndex : IVectorIndex
{
    /// <summary>pgvector's HNSW index supports at most this many dimensions. Larger vectors are stored and searched exactly, without an index.</summary>
    public const int MaxIndexedDimensions = 2000;

    private static readonly Regex TableName = new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly ISqlRunner _runner;
    private readonly int _dimensions;
    private readonly string _table;

    public PgVectorIndex(ISqlRunner runner, int dimensions, string table = "ticket_vectors")
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (dimensions is < 1 or > 16_000)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "pgvector stores 1 to 16,000 dimensions.");
        }

        if (!TableName.IsMatch(table))
        {
            throw new ArgumentException("The table name must be lowercase letters, digits and underscores, starting with a letter or underscore.", nameof(table));
        }

        _runner = runner;
        _dimensions = dimensions;
        _table = table;
    }

    /// <summary>The statements that create the extension, the table and its indexes, in the order to run them.</summary>
    public IReadOnlyList<string> SchemaStatements()
    {
        var statements = new List<string>
        {
            "CREATE EXTENSION IF NOT EXISTS vector",
            $"CREATE TABLE IF NOT EXISTS {_table} (id text PRIMARY KEY, product text NOT NULL, resolved boolean NOT NULL, created_at timestamptz NOT NULL, embedding vector({_dimensions}) NOT NULL)",
            $"CREATE INDEX IF NOT EXISTS {_table}_product_idx ON {_table} (product)",
        };

        if (_dimensions <= MaxIndexedDimensions)
        {
            statements.Add($"CREATE INDEX IF NOT EXISTS {_table}_embedding_idx ON {_table} USING hnsw (embedding vector_cosine_ops)");
        }

        return statements;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        foreach (var statement in SchemaStatements())
        {
            await _runner.ExecuteAsync(statement, Empty, cancellationToken);
        }
    }

    public async Task UpsertAsync(IndexedTicket item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("A ticket needs an id.", nameof(item));
        }

        CheckVector(item.Vector, nameof(item));

        var sql = $"INSERT INTO {_table} (id, product, resolved, created_at, embedding) " +
                  "VALUES (@id, @product, @resolved, @created_at, @embedding::vector) " +
                  "ON CONFLICT (id) DO UPDATE SET product = EXCLUDED.product, resolved = EXCLUDED.resolved, " +
                  "created_at = EXCLUDED.created_at, embedding = EXCLUDED.embedding";

        await _runner.ExecuteAsync(sql, new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["product"] = item.Product,
            ["resolved"] = item.Resolved,
            ["created_at"] = Utc(item.CreatedAt),
            ["embedding"] = Literal(item.Vector),
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, int k, IndexFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        CheckVector(query, nameof(query));

        // Cosine distance to a zero vector is undefined, so there is nothing to ask the database.
        if (k <= 0 || Vectors.IsZero(query))
        {
            return [];
        }

        var sql = $"SELECT id, 1 - (embedding <=> @query::vector) AS score FROM {_table} " +
                  "WHERE (@product::text IS NULL OR product = @product::text) " +
                  "AND (@resolved_only::boolean = false OR resolved) " +
                  "AND (@created_after::timestamptz IS NULL OR created_at >= @created_after::timestamptz) " +
                  "AND (@created_before::timestamptz IS NULL OR created_at < @created_before::timestamptz) " +
                  "AND (@exclude_id::text IS NULL OR id <> @exclude_id::text) " +
                  "ORDER BY embedding <=> @query::vector, id " +
                  "LIMIT @k";

        var rows = await _runner.QueryAsync(sql, new Dictionary<string, object?>
        {
            ["query"] = Literal(query),
            ["product"] = filter.Product,
            ["resolved_only"] = filter.ResolvedOnly,
            ["created_after"] = filter.CreatedAfter is { } after ? Utc(after) : null,
            ["created_before"] = filter.CreatedBefore is { } before ? Utc(before) : null,
            ["exclude_id"] = filter.ExcludeId,
            ["k"] = Math.Min(k, 1000),
        }, cancellationToken);

        return rows
            .Select(r => new VectorHit(Convert.ToString(r["id"], CultureInfo.InvariantCulture)!, Convert.ToDouble(r["score"], CultureInfo.InvariantCulture)))
            .ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        await _runner.ExecuteAsync($"DELETE FROM {_table} WHERE id = @id", new Dictionary<string, object?> { ["id"] = id }, cancellationToken) > 0;

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _runner.QueryAsync($"SELECT count(*) AS n FROM {_table}", Empty, cancellationToken);
        return rows.Count == 0 ? 0 : Convert.ToInt32(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    /// <summary>pgvector's text form of a vector: "[0.1,0.2,0.3]", with a dot as the decimal separator whatever the machine's culture.</summary>
    public static string Literal(float[] vector) =>
        "[" + string.Join(",", vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private void CheckVector(float[] vector, string name)
    {
        if (!Vectors.IsFinite(vector))
        {
            throw new ArgumentException("The vector is empty or contains NaN or infinity.", name);
        }

        if (vector.Length != _dimensions)
        {
            throw new ArgumentException($"This index holds {_dimensions}-dimension vectors and the vector has {vector.Length}.", name);
        }
    }

    /// <summary>timestamptz needs an unambiguous instant. A date with no kind is taken to be UTC.</summary>
    private static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };
}
