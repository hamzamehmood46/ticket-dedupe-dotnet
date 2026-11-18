using System.Globalization;
using Dedupe.Core;
using Dedupe.PgVector;

namespace Dedupe.Core.Tests;

/// <summary>
/// These check the SQL and parameters the adapter produces, using a fake runner. Nothing here talks to a real
/// Postgres, so they show the statements are built correctly and safely, not that a live database accepts them.
/// </summary>
public class PgVectorTests
{
    private sealed class FakeRunner : ISqlRunner
    {
        public List<(string Sql, IReadOnlyDictionary<string, object?> Parameters)> Calls { get; } = [];

        public int ExecuteResult { get; set; } = 1;

        public List<IReadOnlyDictionary<string, object?>> Rows { get; } = [];

        public Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
        {
            Calls.Add((sql, parameters));
            return Task.FromResult(ExecuteResult);
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sql, parameters));
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Rows.ToList());
        }

        public (string Sql, IReadOnlyDictionary<string, object?> Parameters) Last => Calls[^1];
    }

    private static readonly float[] V3 = [0.5f, 0.25f, 0.125f];

    private static (PgVectorIndex Index, FakeRunner Runner) Make(int dims = 3, string table = "ticket_vectors")
    {
        var runner = new FakeRunner();
        return (new PgVectorIndex(runner, dims, table), runner);
    }

    // ---- construction ----

    [Theory]
    [InlineData("tickets; DROP TABLE users")]
    [InlineData("Tickets")]
    [InlineData("1tickets")]
    [InlineData("my-table")]
    [InlineData("my table")]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("public.tickets")]
    public void Table_names_that_could_inject_SQL_are_refused(string table)
    {
        Assert.Throws<ArgumentException>(() => new PgVectorIndex(new FakeRunner(), 3, table));
    }

    [Theory]
    [InlineData("tickets")]
    [InlineData("_tickets_2")]
    [InlineData("ticket_vectors")]
    public void Ordinary_table_names_are_accepted(string table)
    {
        _ = new PgVectorIndex(new FakeRunner(), 3, table);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16_001)]
    public void The_dimension_count_is_range_checked(int dims)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PgVectorIndex(new FakeRunner(), dims));
    }

    [Fact]
    public void A_runner_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new PgVectorIndex(null!, 3));
    }

    // ---- schema ----

    [Fact]
    public void The_schema_creates_the_extension_first_then_the_table()
    {
        var (index, _) = Make(1024, "tickets");
        var statements = index.SchemaStatements();
        Assert.Equal("CREATE EXTENSION IF NOT EXISTS vector", statements[0]);
        Assert.Contains("CREATE TABLE IF NOT EXISTS tickets", statements[1]);
        Assert.Contains("embedding vector(1024) NOT NULL", statements[1]);
        Assert.Contains("id text PRIMARY KEY", statements[1]);
        Assert.Contains("created_at timestamptz NOT NULL", statements[1]);
    }

    [Fact]
    public void Up_to_the_HNSW_limit_the_schema_adds_a_cosine_index()
    {
        var (index, _) = Make(PgVectorIndex.MaxIndexedDimensions);
        Assert.Contains(index.SchemaStatements(), s => s.Contains("USING hnsw (embedding vector_cosine_ops)"));
    }

    [Fact]
    public void Above_the_HNSW_limit_there_is_no_approximate_index_and_search_stays_exact()
    {
        // The default embedder uses 2,048 dimensions, which pgvector's HNSW index cannot hold.
        var (index, _) = Make(HashingEmbedder.DefaultDimensions);
        Assert.True(HashingEmbedder.DefaultDimensions > PgVectorIndex.MaxIndexedDimensions);
        Assert.DoesNotContain(index.SchemaStatements(), s => s.Contains("hnsw"));
        Assert.Contains(index.SchemaStatements(), s => s.Contains("vector(2048)"));
    }

    [Fact]
    public void The_schema_indexes_the_product_column_that_filters_use()
    {
        var (index, _) = Make();
        Assert.Contains(index.SchemaStatements(), s => s.Contains("ticket_vectors_product_idx") && s.Contains("(product)"));
    }

    [Fact]
    public async Task EnsureSchema_runs_every_statement_in_order()
    {
        var (index, runner) = Make();
        await index.EnsureSchemaAsync();
        Assert.Equal(index.SchemaStatements(), runner.Calls.Select(c => c.Sql));
        Assert.All(runner.Calls, c => Assert.Empty(c.Parameters));
    }

    // ---- upsert ----

    [Fact]
    public async Task Upsert_is_an_insert_on_conflict_update_with_every_value_as_a_parameter()
    {
        var (index, runner) = Make();
        var when = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        await index.UpsertAsync(new IndexedTicket("T-0001", "Accounts", true, when, V3));

        var (sql, p) = runner.Last;
        Assert.Contains("INSERT INTO ticket_vectors", sql);
        Assert.Contains("ON CONFLICT (id) DO UPDATE SET", sql);
        Assert.Contains("@embedding::vector", sql);
        Assert.DoesNotContain("T-0001", sql);
        Assert.DoesNotContain("Accounts", sql);
        Assert.Equal("T-0001", p["id"]);
        Assert.Equal("Accounts", p["product"]);
        Assert.Equal(true, p["resolved"]);
        Assert.Equal(when, p["created_at"]);
        Assert.Equal("[0.5,0.25,0.125]", p["embedding"]);
    }

    [Fact]
    public async Task A_hostile_ticket_id_or_product_is_a_parameter_never_part_of_the_SQL()
    {
        var (index, runner) = Make();
        const string evil = "x'); DROP TABLE ticket_vectors; --";
        await index.UpsertAsync(new IndexedTicket(evil, evil, false, DateTime.UtcNow, V3));
        Assert.DoesNotContain("DROP", runner.Last.Sql);
        Assert.Equal(evil, runner.Last.Parameters["id"]);
        Assert.Equal(evil, runner.Last.Parameters["product"]);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc, 0)]
    [InlineData(DateTimeKind.Unspecified, 0)]
    public async Task Dates_are_sent_as_UTC(DateTimeKind kind, int offsetHours)
    {
        var (index, runner) = Make();
        var when = new DateTime(2026, 7, 4, 12, 0, 0, kind);
        await index.UpsertAsync(new IndexedTicket("a", "p", true, when, V3));
        var sent = (DateTime)runner.Last.Parameters["created_at"]!;
        Assert.Equal(DateTimeKind.Utc, sent.Kind);
        Assert.Equal(when.AddHours(offsetHours).Ticks, sent.Ticks);
    }

    [Fact]
    public async Task A_local_date_is_converted_to_UTC()
    {
        var (index, runner) = Make();
        var local = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Local);
        await index.UpsertAsync(new IndexedTicket("a", "p", true, local, V3));
        var sent = (DateTime)runner.Last.Parameters["created_at"]!;
        Assert.Equal(DateTimeKind.Utc, sent.Kind);
        Assert.Equal(local.ToUniversalTime(), sent);
    }

    [Fact]
    public async Task Upsert_refuses_bad_input_before_touching_the_database()
    {
        var (index, runner) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(new IndexedTicket("a", "p", true, DateTime.UtcNow, [1, 2])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(new IndexedTicket("a", "p", true, DateTime.UtcNow, [1, float.NaN, 3])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(new IndexedTicket(" ", "p", true, DateTime.UtcNow, V3)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.UpsertAsync(null!));
        Assert.Empty(runner.Calls);
    }

    // ---- search ----

    [Fact]
    public async Task Search_orders_by_cosine_distance_then_id_and_limits_rows()
    {
        var (index, runner) = Make();
        await index.SearchAsync(V3, 5, new IndexFilter());
        var sql = runner.Last.Sql;
        Assert.Contains("1 - (embedding <=> @query::vector) AS score", sql);
        Assert.Contains("ORDER BY embedding <=> @query::vector, id", sql);
        Assert.EndsWith("LIMIT @k", sql);
        Assert.Equal(5, runner.Last.Parameters["k"]);
    }

    [Fact]
    public async Task Every_filter_is_applied_in_the_WHERE_clause_with_typed_casts_so_nulls_work()
    {
        var (index, runner) = Make();
        await index.SearchAsync(V3, 3, new IndexFilter());
        var sql = runner.Last.Sql;
        Assert.Contains("(@product::text IS NULL OR product = @product::text)", sql);
        Assert.Contains("(@resolved_only::boolean = false OR resolved)", sql);
        Assert.Contains("(@created_after::timestamptz IS NULL OR created_at >= @created_after::timestamptz)", sql);
        Assert.Contains("(@created_before::timestamptz IS NULL OR created_at < @created_before::timestamptz)", sql);
        Assert.Contains("(@exclude_id::text IS NULL OR id <> @exclude_id::text)", sql);
    }

    [Fact]
    public async Task Unset_filters_are_null_parameters()
    {
        var (index, runner) = Make();
        await index.SearchAsync(V3, 3, new IndexFilter());
        var p = runner.Last.Parameters;
        Assert.Null(p["product"]);
        Assert.Null(p["created_after"]);
        Assert.Null(p["created_before"]);
        Assert.Null(p["exclude_id"]);
        Assert.Equal(false, p["resolved_only"]);
    }

    [Fact]
    public async Task Filter_values_are_passed_as_parameters_and_never_written_into_the_SQL()
    {
        var (index, runner) = Make();
        const string evil = "Billing' OR '1'='1";
        var after = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await index.SearchAsync(V3, 3, new IndexFilter(evil, true, after, before, "T-0042; DROP TABLE x"));

        Assert.DoesNotContain("OR '1'='1", runner.Last.Sql);
        Assert.DoesNotContain("DROP", runner.Last.Sql);
        Assert.DoesNotContain("T-0042", runner.Last.Sql);
        var p = runner.Last.Parameters;
        Assert.Equal(evil, p["product"]);
        Assert.Equal(true, p["resolved_only"]);
        Assert.Equal(after, p["created_after"]);
        Assert.Equal(before, p["created_before"]);
        Assert.Equal("T-0042; DROP TABLE x", p["exclude_id"]);
    }

    [Fact]
    public async Task The_query_vector_is_sent_as_text_and_cast_in_SQL()
    {
        var (index, runner) = Make();
        await index.SearchAsync(V3, 3, new IndexFilter());
        Assert.Equal("[0.5,0.25,0.125]", runner.Last.Parameters["query"]);
    }

    [Fact]
    public async Task A_huge_k_is_capped()
    {
        var (index, runner) = Make();
        await index.SearchAsync(V3, int.MaxValue, new IndexFilter());
        Assert.Equal(1000, runner.Last.Parameters["k"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task A_k_of_zero_or_less_does_not_query(int k)
    {
        var (index, runner) = Make();
        Assert.Empty(await index.SearchAsync(V3, k, new IndexFilter()));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_zero_query_vector_does_not_query_because_cosine_distance_to_it_is_undefined()
    {
        var (index, runner) = Make();
        Assert.Empty(await index.SearchAsync([0, 0, 0], 5, new IndexFilter()));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Search_refuses_a_vector_of_the_wrong_size_or_with_bad_numbers()
    {
        var (index, runner) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync([1, 2], 3, new IndexFilter()));
        await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync([1, float.PositiveInfinity, 0], 3, new IndexFilter()));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Rows_become_hits_in_the_order_the_database_returned_them()
    {
        var (index, runner) = Make();
        runner.Rows.Add(new Dictionary<string, object?> { ["id"] = "T-0007", ["score"] = 0.91 });
        runner.Rows.Add(new Dictionary<string, object?> { ["id"] = "T-0002", ["score"] = 0.5f });
        runner.Rows.Add(new Dictionary<string, object?> { ["id"] = "T-0003", ["score"] = 1m });

        var hits = await index.SearchAsync(V3, 5, new IndexFilter());
        Assert.Equal(["T-0007", "T-0002", "T-0003"], hits.Select(h => h.Id));
        Assert.Equal([0.91, 0.5, 1.0], hits.Select(h => h.Score));
    }

    [Fact]
    public async Task No_rows_means_no_hits()
    {
        var (index, _) = Make();
        Assert.Empty(await index.SearchAsync(V3, 5, new IndexFilter()));
    }

    // ---- delete and count ----

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task Delete_reports_whether_a_row_was_removed(int affected, bool expected)
    {
        var (index, runner) = Make();
        runner.ExecuteResult = affected;
        Assert.Equal(expected, await index.DeleteAsync("T-0001"));
        Assert.Equal("DELETE FROM ticket_vectors WHERE id = @id", runner.Last.Sql);
        Assert.Equal("T-0001", runner.Last.Parameters["id"]);
    }

    [Fact]
    public async Task Count_reads_the_row_count()
    {
        var (index, runner) = Make();
        runner.Rows.Add(new Dictionary<string, object?> { ["n"] = 100L });
        Assert.Equal(100, await index.CountAsync());
        Assert.Equal("SELECT count(*) AS n FROM ticket_vectors", runner.Last.Sql);
    }

    [Fact]
    public async Task Count_of_nothing_is_zero()
    {
        var (index, _) = Make();
        Assert.Equal(0, await index.CountAsync());
    }

    [Fact]
    public async Task A_custom_table_name_is_used_everywhere()
    {
        var (index, runner) = Make(3, "support_tickets");
        await index.UpsertAsync(new IndexedTicket("a", "p", true, DateTime.UtcNow, V3));
        await index.SearchAsync(V3, 1, new IndexFilter());
        await index.DeleteAsync("a");
        await index.CountAsync();
        Assert.All(runner.Calls, c => Assert.Contains("support_tickets", c.Sql));
        Assert.All(runner.Calls, c => Assert.DoesNotContain("ticket_vectors", c.Sql));
    }

    // ---- the vector literal ----

    [Fact]
    public void Vector_literal_format()
    {
        Assert.Equal("[1,2,3]", PgVectorIndex.Literal([1, 2, 3]));
        Assert.Equal("[0.1,-0.5,0]", PgVectorIndex.Literal([0.1f, -0.5f, 0]));
    }

    [Fact]
    public void Vector_literals_use_a_dot_whatever_the_machines_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("[0.5,0.25]", PgVectorIndex.Literal([0.5f, 0.25f]));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void A_float_survives_a_round_trip_through_the_literal()
    {
        var rng = new Random(3);
        var v = Enumerable.Range(0, 200).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var parsed = PgVectorIndex.Literal(v).Trim('[', ']').Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(v, parsed);
    }

    [Fact]
    public void The_default_embedders_vectors_produce_a_literal_of_the_right_length()
    {
        var literal = PgVectorIndex.Literal(new HashingEmbedder().Embed("refund missing"));
        Assert.Equal(HashingEmbedder.DefaultDimensions, literal.Trim('[', ']').Split(',').Length);
    }
}
