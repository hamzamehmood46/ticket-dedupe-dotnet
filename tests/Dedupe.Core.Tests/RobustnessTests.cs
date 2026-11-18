using System.Diagnostics;
using Dedupe.Core;

namespace Dedupe.Core.Tests;

public class RobustnessTests
{
    [Fact]
    public async Task Random_text_never_breaks_cleaning_embedding_or_searching()
    {
        var finder = await DuplicateFinder.CreateAsync(SampleTickets.All);
        var rng = new Random(2026);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz 0123456789\n\r\t>@#-:.,!?'\"*()[]{}<>\\/ é中😀";

        for (var i = 0; i < 600; i++)
        {
            string Random(int max) => new(Enumerable.Range(0, rng.Next(0, max)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

            var result = await finder.FindAsync(new NewTicket(Random(80), Random(600), rng.Next(3) == 0 ? SampleTickets.DemoNow : null, rng.Next(5) == 0 ? "T-0001" : null));
            Assert.True(result.Matches.Count <= finder.Options.MaxResults);
            Assert.All(result.Matches, m => Assert.InRange(m.Score, 0.0, 1.0 + 1e-9));
            Assert.All(result.Matches, m => Assert.NotEqual(Verdict.None, m.Verdict));
        }
    }

    [Fact]
    public async Task Random_edits_to_real_tickets_still_find_the_original_most_of_the_time()
    {
        // Damage a sample ticket a little (a deleted or swapped character) and ask for its duplicates. Typos should not
        // stop the right group from coming back for most tickets.
        var finder = await DuplicateFinder.CreateAsync(SampleTickets.All);
        var rng = new Random(5);
        var tried = 0;
        var found = 0;
        foreach (var t in SampleTickets.All.Where(t => t.GroupId is not null).OrderByDescending(t => t.CreatedAt).Take(40))
        {
            var chars = (t.Subject + ". " + t.Body).ToCharArray();
            var at = rng.Next(chars.Length);
            chars[at] = (char)('a' + rng.Next(26));
            var result = await finder.FindAsync(new NewTicket(new string(chars), "", t.CreatedAt.AddMinutes(1), t.Id));
            tried++;
            found += result.Matches.Any(m => m.Ticket.GroupId == t.GroupId) ? 1 : 0;
        }

        Assert.Equal(40, tried);
        Assert.True(found >= 36, $"found {found} of {tried}");
    }

    [Fact]
    public async Task A_very_long_ticket_is_handled()
    {
        var finder = await DuplicateFinder.CreateAsync(SampleTickets.All);
        var body = string.Join(" ", Enumerable.Repeat("my account is locked after too many failed login attempts", 5_000));
        var result = await finder.FindAsync(new NewTicket("Locked out", body, SampleTickets.DemoNow));
        Assert.True(result.CleanedText.Length <= TicketCleaner.MaxChars);
        Assert.Equal("ACC-LOCK", result.Matches[0].Ticket.GroupId);
    }

    [Fact]
    public async Task Two_thousand_tickets_index_and_search_in_a_few_seconds()
    {
        var tickets = new List<Ticket>(SampleTickets.All);
        for (var i = 0; i < 1_900; i++)
        {
            var basis = SampleTickets.All[i % SampleTickets.All.Count];
            tickets.Add(basis with { Id = $"X-{i:0000}", Subject = basis.Subject + " " + (i % 7), GroupId = null });
        }

        var watch = Stopwatch.StartNew();
        var finder = await DuplicateFinder.CreateAsync(tickets);
        for (var q = 0; q < 20; q++)
        {
            var result = await finder.FindAsync(SampleTickets.Demos[q % SampleTickets.Demos.Count].Ticket);
            Assert.Equal(2_000, result.IndexSize);
        }

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Cleaning_is_fast_on_hostile_input()
    {
        var watch = Stopwatch.StartNew();
        var nasty = string.Concat(Enumerable.Repeat("a1b2c3d4-", 20_000)) + string.Concat(Enumerable.Repeat("> ", 20_000)) + new string('9', 100_000);
        _ = TicketCleaner.Clean(nasty, nasty);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Concurrent_searches_and_additions_do_not_corrupt_anything()
    {
        var finder = await DuplicateFinder.CreateAsync(SampleTickets.All);
        var adds = Enumerable.Range(0, 100).Select(i => Task.Run(() => finder.AddAsync(
            new Ticket($"N-{i:000}", "Accounts", "Locked out of account " + i, "Too many failed attempts", SampleTickets.DemoNow.AddMinutes(-i), TicketStatus.Resolved, "Unlocked.", null))));
        var finds = Enumerable.Range(0, 100).Select(_ => Task.Run(() => finder.FindAsync(SampleTickets.Demos[0].Ticket)));
        await Task.WhenAll(adds.Concat<Task>(finds));

        var after = await finder.FindAsync(SampleTickets.Demos[0].Ticket);
        Assert.Equal(200, after.IndexSize);
    }
}
