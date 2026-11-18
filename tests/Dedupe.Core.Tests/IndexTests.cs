using Dedupe.Core;

namespace Dedupe.Core.Tests;

public class IndexTests
{
    private static readonly DateTime Day = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IndexedTicket Item(string id, float[] v, string product = "Accounts", bool resolved = true, int day = 0) =>
        new(id, product, resolved, Day.AddDays(day), v);

    private static async Task<FlatVectorIndex> Seeded()
    {
        var index = new FlatVectorIndex();
        await index.UpsertAsync(Item("a", [1, 0, 0], "Accounts", true, 1));
        await index.UpsertAsync(Item("b", [0.9f, 0.1f, 0], "Billing", false, 2));
        await index.UpsertAsync(Item("c", [0.5f, 0.5f, 0], "Accounts", false, 3));
        await index.UpsertAsync(Item("d", [0, 1, 0], "Billing", true, 4));
        await index.UpsertAsync(Item("e", [0, 0, 1], "Mobile", true, 5));
        return index;
    }

    private static string[] Ids(IEnumerable<VectorHit> hits) => hits.Select(h => h.Id).ToArray();

    [Fact]
    public async Task Best_match_first()
    {
        var hits = await (await Seeded()).SearchAsync([1, 0, 0], 3, new IndexFilter());
        Assert.Equal(["a", "b", "c"], Ids(hits));
        Assert.Equal(1.0, hits[0].Score, 6);
        Assert.True(hits[0].Score > hits[1].Score && hits[1].Score > hits[2].Score);
    }

    [Fact]
    public async Task K_limits_results_and_zero_or_negative_k_gives_none()
    {
        var index = await Seeded();
        Assert.Equal(2, (await index.SearchAsync([1, 0, 0], 2, new IndexFilter())).Count);
        Assert.Empty(await index.SearchAsync([1, 0, 0], 0, new IndexFilter()));
        Assert.Empty(await index.SearchAsync([1, 0, 0], -5, new IndexFilter()));
        Assert.Equal(5, (await index.SearchAsync([1, 0, 0], 100, new IndexFilter())).Count);
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_id_so_results_repeat()
    {
        var index = new FlatVectorIndex();
        foreach (var id in new[] { "z", "m", "b", "q" })
        {
            await index.UpsertAsync(Item(id, [1, 1, 0]));
        }

        Assert.Equal(["b", "m", "q", "z"], Ids(await index.SearchAsync([1, 1, 0], 4, new IndexFilter())));
    }

    [Fact]
    public async Task Product_filter()
    {
        var hits = await (await Seeded()).SearchAsync([1, 0, 0], 10, new IndexFilter(Product: "Billing"));
        Assert.Equal(["b", "d"], Ids(hits));
    }

    [Fact]
    public async Task Resolved_only_filter()
    {
        var hits = await (await Seeded()).SearchAsync([1, 0, 0], 10, new IndexFilter(ResolvedOnly: true));
        Assert.Equal(["a", "d", "e"], Ids(hits).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Date_filters_include_the_start_and_exclude_the_end()
    {
        var index = await Seeded();
        Assert.Equal(["c", "d"], Ids(await index.SearchAsync([1, 1, 0], 10, new IndexFilter(CreatedAfter: Day.AddDays(3), CreatedBefore: Day.AddDays(5)))).OrderBy(x => x).ToArray());
        Assert.Equal(["a"], Ids(await index.SearchAsync([1, 0, 0], 10, new IndexFilter(CreatedBefore: Day.AddDays(2)))));
    }

    [Fact]
    public async Task Excluded_id_never_comes_back()
    {
        var hits = await (await Seeded()).SearchAsync([1, 0, 0], 10, new IndexFilter(ExcludeId: "a"));
        Assert.DoesNotContain("a", Ids(hits));
    }

    [Fact]
    public async Task Filters_apply_before_ranking_so_k_is_still_filled()
    {
        // The two best matches for [1,0,0] are a and b. Excluding them must return the next two, not an empty or short list.
        var hits = await (await Seeded()).SearchAsync([1, 0, 0], 2, new IndexFilter(ExcludeId: "a", Product: "Accounts"));
        Assert.Equal(["c"], Ids(hits));

        var more = await (await Seeded()).SearchAsync([1, 0, 0], 2, new IndexFilter(ExcludeId: "a"));
        Assert.Equal(2, more.Count);
        Assert.Equal("b", more[0].Id);
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        var hits = await (await Seeded()).SearchAsync([1, 1, 0], 10, new IndexFilter(Product: "Billing", ResolvedOnly: true, CreatedAfter: Day.AddDays(3)));
        Assert.Equal(["d"], Ids(hits));
    }

    [Fact]
    public async Task A_zero_query_matches_nothing()
    {
        Assert.Empty(await (await Seeded()).SearchAsync([0, 0, 0], 5, new IndexFilter()));
    }

    [Fact]
    public async Task Searching_an_empty_index_is_fine()
    {
        Assert.Empty(await new FlatVectorIndex().SearchAsync([1, 2, 3], 5, new IndexFilter()));
    }

    [Fact]
    public async Task Upsert_replaces_an_existing_id()
    {
        var index = await Seeded();
        await index.UpsertAsync(Item("a", [0, 0, 1]));
        Assert.Equal(5, await index.CountAsync());

        // "a" now points the same way as "e", and no longer matches the old direction.
        Assert.Equal(1.0, (await index.SearchAsync([0, 0, 1], 5, new IndexFilter())).Single(h => h.Id == "a").Score, 6);
        Assert.True((await index.SearchAsync([1, 0, 0], 5, new IndexFilter())).Single(h => h.Id == "a").Score < 0.01);
    }

    [Fact]
    public async Task Delete_removes_and_reports_whether_it_found_anything()
    {
        var index = await Seeded();
        Assert.True(await index.DeleteAsync("a"));
        Assert.False(await index.DeleteAsync("a"));
        Assert.False(await index.DeleteAsync("nope"));
        Assert.Equal(4, await index.CountAsync());
        Assert.DoesNotContain("a", Ids(await index.SearchAsync([1, 0, 0], 10, new IndexFilter())));
    }

    [Fact]
    public async Task The_index_keeps_its_own_copy_of_a_vector()
    {
        var index = new FlatVectorIndex();
        var v = new float[] { 1, 0, 0 };
        await index.UpsertAsync(Item("a", v));
        v[0] = 0;
        v[2] = 1;
        Assert.Equal(1.0, (await index.SearchAsync([1, 0, 0], 1, new IndexFilter())).Single().Score, 6);
    }

    [Fact]
    public async Task A_vector_of_the_wrong_size_is_refused_on_upsert_and_on_search()
    {
        var index = await Seeded();
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Item("x", [1, 2])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync([1, 2], 3, new IndexFilter()));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task Non_finite_vectors_are_refused(float bad)
    {
        var index = new FlatVectorIndex();
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Item("x", [1, bad])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync([1, bad], 3, new IndexFilter()));
    }

    [Fact]
    public async Task Empty_vectors_and_blank_ids_are_refused()
    {
        var index = new FlatVectorIndex();
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Item("x", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Item("  ", [1])));
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Item("", [1])));
    }

    [Fact]
    public async Task Parallel_writes_and_reads_stay_consistent()
    {
        var index = new FlatVectorIndex();
        var writes = Enumerable.Range(0, 400).Select(i => Task.Run(() => index.UpsertAsync(Item($"t{i:000}", [i + 1, 1, 0]))));
        var reads = Enumerable.Range(0, 100).Select(_ => Task.Run(() => index.SearchAsync([1, 1, 0], 5, new IndexFilter())));
        await Task.WhenAll(writes.Concat<Task>(reads));
        Assert.Equal(400, await index.CountAsync());
    }

    [Fact]
    public async Task Exact_search_agrees_with_a_brute_force_check_on_random_data()
    {
        var rng = new Random(11);
        var index = new FlatVectorIndex();
        var all = new List<IndexedTicket>();
        for (var i = 0; i < 300; i++)
        {
            var v = Enumerable.Range(0, 16).Select(_ => (float)rng.NextDouble()).ToArray();
            var item = Item($"t{i:000}", v, i % 2 == 0 ? "Accounts" : "Billing", i % 3 == 0, i);
            all.Add(item);
            await index.UpsertAsync(item);
        }

        for (var q = 0; q < 20; q++)
        {
            var query = Enumerable.Range(0, 16).Select(_ => (float)rng.NextDouble()).ToArray();
            var filter = new IndexFilter(Product: q % 2 == 0 ? "Billing" : null, ResolvedOnly: q % 3 == 0);
            var expected = all
                .Where(t => (filter.Product is null || t.Product == filter.Product) && (!filter.ResolvedOnly || t.Resolved))
                .OrderByDescending(t => Vectors.Cosine(query, t.Vector)).ThenBy(t => t.Id, StringComparer.Ordinal)
                .Take(7).Select(t => t.Id).ToArray();
            Assert.Equal(expected, Ids(await index.SearchAsync(query, 7, filter)));
        }
    }
}
