using System.Text.RegularExpressions;
using Dedupe.Core;

namespace Dedupe.Core.Tests;

public class EvaluatorTests
{
    private static readonly EvaluationReport Report = Evaluator.Run(SampleTickets.All);

    private static ThresholdPoint P(double t, int tp, int fp, int fn) => new(t, tp, fp, fn);

    [Fact]
    public void Held_out_tickets_are_every_third_group_and_every_third_one_off()
    {
        var (dev, test) = Evaluator.Split(SampleTickets.All);
        Assert.Equal(70, dev.Count);
        Assert.Equal(30, test.Count);

        var order = SampleTickets.All.Where(t => t.GroupId is not null).Select(t => t.GroupId!).Distinct().ToList();
        var heldOut = order.Where((_, i) => i % 3 == 2).ToHashSet();
        Assert.Equal(heldOut, test.Where(t => t.GroupId is not null).Select(t => t.GroupId!).ToHashSet());
    }

    [Fact]
    public void A_duplicate_group_is_never_split_across_the_two_sides()
    {
        var (dev, test) = Evaluator.Split(SampleTickets.All);
        var overlap = dev.Where(t => t.GroupId is not null).Select(t => t.GroupId).Intersect(test.Where(t => t.GroupId is not null).Select(t => t.GroupId));
        Assert.Empty(overlap);
        Assert.Empty(dev.Select(t => t.Id).Intersect(test.Select(t => t.Id)));
        Assert.Equal(100, dev.Count + test.Count);
    }

    [Fact]
    public void The_held_out_half_has_real_duplicates_to_find()
    {
        Assert.Equal(70, Report.DevTickets);
        Assert.Equal(30, Report.TestTickets);
        Assert.True(Report.TestDuplicatePairs >= 20);
        Assert.True(Report.DevDuplicatePairs >= 40);
    }

    [Fact]
    public void Thresholds_are_chosen_from_the_development_half_to_meet_the_precision_target()
    {
        Assert.True(Report.PrecisionTargetMet);
        Assert.InRange(Report.DuplicateThreshold, 0.15, 0.45);
        Assert.True(Report.RelatedThreshold <= Report.DuplicateThreshold);
        var chosen = Report.DevSweep.Single(p => Math.Abs(p.Threshold - Report.DuplicateThreshold) < 1e-9);
        Assert.True(chosen.Precision >= 0.95);
        Assert.True(chosen.TruePositives > 0);

        // And it is the lowest threshold that does.
        Assert.DoesNotContain(Report.DevSweep.Where(p => p.Threshold < Report.DuplicateThreshold), p => p.TruePositives > 0 && p.Precision >= 0.95);
    }

    [Fact]
    public void On_the_held_out_half_few_merges_are_wrong_and_most_duplicates_are_found_at_the_related_threshold()
    {
        Assert.True(Report.TestAtDuplicate.Precision >= 0.9, $"precision {Report.TestAtDuplicate.Precision:0.00}");
        Assert.True(Report.TestAtDuplicate.Recall >= 0.5, $"recall {Report.TestAtDuplicate.Recall:0.00}");
        Assert.True(Report.TestAtRelated.Recall >= 0.9, $"related recall {Report.TestAtRelated.Recall:0.00}");
    }

    [Fact]
    public void The_nearest_duplicate_ranks_first_for_almost_every_held_out_ticket()
    {
        Assert.True(Report.RankedQueries >= 20);
        Assert.True(Report.HitAt1 >= 0.9, $"hit@1 {Report.HitAt1:0.00}");
        Assert.True(Report.HitAt3 >= Report.HitAt1);
        Assert.True(Report.Mrr >= 0.9, $"mrr {Report.Mrr:0.00}");
        Assert.InRange(Report.Mrr, Report.HitAt1 / 2, 1.0);
    }

    [Fact]
    public void The_sweep_covers_zero_to_one_and_moves_in_the_right_direction()
    {
        Assert.Equal(101, Report.DevSweep.Count);
        Assert.Equal(0.0, Report.DevSweep[0].Threshold);
        Assert.Equal(1.0, Report.DevSweep[^1].Threshold, 9);

        for (var i = 1; i < Report.DevSweep.Count; i++)
        {
            var (a, b) = (Report.DevSweep[i - 1], Report.DevSweep[i]);
            Assert.True(b.TruePositives <= a.TruePositives);
            Assert.True(b.FalsePositives <= a.FalsePositives);
            Assert.True(b.FalseNegatives >= a.FalseNegatives);
            Assert.Equal(Report.DevDuplicatePairs, b.TruePositives + b.FalseNegatives);
        }

        Assert.Equal(0, Report.DevSweep[0].FalseNegatives);
        Assert.Equal(Report.DevDuplicatePairs, Report.DevSweep[^1].FalseNegatives);
        Assert.All(Report.TestSweep, p => Assert.Equal(Report.TestDuplicatePairs, p.TruePositives + p.FalseNegatives));
    }

    [Fact]
    public void Listed_mistakes_really_are_mistakes()
    {
        var bySubject = SampleTickets.All.ToDictionary(t => t.Id);
        Assert.All(Report.FalsePositives, e =>
        {
            Assert.True(e.Score >= Report.DuplicateThreshold);
            Assert.False(bySubject[e.IdA].GroupId is not null && bySubject[e.IdA].GroupId == bySubject[e.IdB].GroupId);
        });
        Assert.All(Report.FalseNegatives, e =>
        {
            Assert.True(e.Score < Report.DuplicateThreshold);
            Assert.Equal(bySubject[e.IdA].GroupId, bySubject[e.IdB].GroupId);
            Assert.NotNull(bySubject[e.IdA].GroupId);
        });
        Assert.True(Report.FalseNegatives.Count <= 5 && Report.FalsePositives.Count <= 5);
    }

    [Fact]
    public void The_missed_duplicates_are_listed_weakest_first()
    {
        Assert.Equal(Report.FalseNegatives.OrderBy(e => e.Score).Select(e => e.Score), Report.FalseNegatives.Select(e => e.Score));
    }

    [Fact]
    public void Nothing_from_the_held_out_tickets_leaks_into_the_development_numbers()
    {
        var (_, test) = Evaluator.Split(SampleTickets.All);
        var testIds = test.Select(t => t.Id).ToHashSet();
        var scrambled = SampleTickets.All
            .Select(t => testIds.Contains(t.Id) ? t with { Subject = "zzz qqq " + t.Id, Body = "completely different words about gardening and boats" } : t)
            .ToList();

        var changed = Evaluator.Run(scrambled);
        Assert.Equal(Report.DevSweep, changed.DevSweep);
        Assert.Equal(Report.DuplicateThreshold, changed.DuplicateThreshold);
        Assert.Equal(Report.RelatedThreshold, changed.RelatedThreshold);
        Assert.NotEqual(Report.TestSweep, changed.TestSweep);
    }

    [Fact]
    public void The_evaluation_is_repeatable()
    {
        var again = Evaluator.Run(SampleTickets.All);
        Assert.Equal(Report.DuplicateThreshold, again.DuplicateThreshold);
        Assert.Equal(Report.Mrr, again.Mrr);
        Assert.Equal(Report.TestSweep, again.TestSweep);
    }

    [Fact]
    public void Too_few_tickets_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Evaluator.Run(SampleTickets.All.Take(2).ToList()));
        Assert.Throws<ArgumentException>(() => Evaluator.Run([]));
    }

    [Fact]
    public void With_no_real_duplicates_it_still_runs_and_reports_no_retrieval()
    {
        var singles = SampleTickets.All.Where(t => t.GroupId is null).ToList();
        var report = Evaluator.Run(singles);
        Assert.Equal(0, report.RankedQueries);
        Assert.Equal(0, report.HitAt1);
        Assert.False(report.PrecisionTargetMet);
        Assert.Equal(0, report.DevDuplicatePairs);
    }

    // ---- threshold picking on made-up sweeps ----

    [Fact]
    public void The_duplicate_threshold_is_the_lowest_that_meets_the_precision_target()
    {
        var sweep = new[] { P(0.1, 10, 40, 0), P(0.2, 9, 3, 1), P(0.3, 8, 0, 2), P(0.4, 5, 0, 5) };
        var (t, met) = Evaluator.TuneDuplicate(sweep, 0.95);
        Assert.Equal(0.3, t);
        Assert.True(met);
    }

    [Fact]
    public void A_threshold_with_no_correct_pairs_is_not_picked_just_because_nothing_was_wrong()
    {
        // At 0.9 nothing is called a duplicate, which gives "perfect" precision of 1 and means nothing.
        var sweep = new[] { P(0.1, 5, 20, 0), P(0.5, 3, 2, 2), P(0.9, 0, 0, 5) };
        var (t, met) = Evaluator.TuneDuplicate(sweep, 0.95);
        Assert.False(met);
        Assert.Equal(0.5, t);
    }

    [Fact]
    public void When_the_target_cannot_be_met_the_best_F1_is_used_and_ties_go_to_the_higher_threshold()
    {
        var sweep = new[] { P(0.1, 10, 10, 0), P(0.2, 10, 10, 0), P(0.3, 4, 8, 6) };
        var (t, met) = Evaluator.TuneDuplicate(sweep, 0.95);
        Assert.False(met);
        Assert.Equal(0.2, t);
    }

    [Fact]
    public void The_related_threshold_is_the_highest_that_still_finds_enough_duplicates()
    {
        var sweep = new[] { P(0.1, 10, 50, 0), P(0.2, 9, 20, 1), P(0.3, 8, 5, 2), P(0.4, 5, 0, 5) };
        Assert.Equal(0.2, Evaluator.TuneRelated(sweep, 0.9));
        Assert.Equal(0.3, Evaluator.TuneRelated(sweep, 0.8));
        Assert.Equal(0.1, Evaluator.TuneRelated(sweep, 1.0));
    }

    [Fact]
    public void Precision_recall_and_F1()
    {
        var p = P(0.5, 6, 2, 4);
        Assert.Equal(0.75, p.Precision, 6);
        Assert.Equal(0.6, p.Recall, 6);
        Assert.Equal(2 * 0.75 * 0.6 / 1.35, p.F1, 6);
    }

    [Fact]
    public void Empty_denominators_have_sensible_values()
    {
        Assert.Equal(1, P(0.5, 0, 0, 3).Precision);
        Assert.Equal(1, P(0.5, 0, 2, 0).Recall);
        Assert.Equal(0, P(0.5, 0, 0, 3).F1);
        Assert.Equal(0, P(0.5, 0, 4, 3).F1);
    }
}

public class SampleTicketTests
{
    private static readonly IReadOnlyList<Ticket> All = SampleTickets.All;

    [Fact]
    public void One_hundred_tickets_in_twenty_groups_of_four_plus_twenty_one_offs()
    {
        Assert.Equal(100, All.Count);
        var groups = All.Where(t => t.GroupId is not null).GroupBy(t => t.GroupId).ToList();
        Assert.Equal(20, groups.Count);
        Assert.All(groups, g => Assert.Equal(4, g.Count()));
        Assert.Equal(20, All.Count(t => t.GroupId is null));
    }

    [Fact]
    public void Ids_are_sequential_and_tickets_are_oldest_first()
    {
        Assert.Equal(Enumerable.Range(1, 100).Select(i => $"T-{i:0000}"), All.Select(t => t.Id));
        Assert.Equal(All.OrderBy(t => t.CreatedAt).Select(t => t.Id), All.Select(t => t.Id));
    }

    [Fact]
    public void Every_group_stays_inside_one_product()
    {
        Assert.All(All.Where(t => t.GroupId is not null).GroupBy(t => t.GroupId), g => Assert.Single(g.Select(t => t.Product).Distinct()));
        Assert.Subset(SampleTickets.Products.ToHashSet(), All.Select(t => t.Product).ToHashSet());
        Assert.Equal(SampleTickets.Products.Count, All.Select(t => t.Product).Distinct().Count());
    }

    [Fact]
    public void In_each_group_the_two_oldest_are_resolved_and_the_two_newest_are_open()
    {
        foreach (var g in All.Where(t => t.GroupId is not null).GroupBy(t => t.GroupId))
        {
            var ordered = g.OrderBy(t => t.CreatedAt).ToList();
            Assert.All(ordered.Take(2), t => { Assert.Equal(TicketStatus.Resolved, t.Status); Assert.False(string.IsNullOrWhiteSpace(t.Resolution)); });
            Assert.All(ordered.Skip(2), t => { Assert.Equal(TicketStatus.Open, t.Status); Assert.Null(t.Resolution); });
        }
    }

    [Fact]
    public void Resolved_tickets_have_a_resolution_and_open_ones_do_not()
    {
        Assert.All(All.Where(t => t.Status == TicketStatus.Resolved), t => Assert.False(string.IsNullOrWhiteSpace(t.Resolution)));
        Assert.All(All.Where(t => t.Status == TicketStatus.Open), t => Assert.Null(t.Resolution));
    }

    [Fact]
    public void Subjects_are_unique_and_nothing_is_blank()
    {
        Assert.Equal(All.Count, All.Select(t => t.Subject).Distinct().Count());
        Assert.All(All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Subject));
            Assert.False(string.IsNullOrWhiteSpace(t.Body));
        });
    }

    [Fact]
    public void The_samples_contain_no_personal_data()
    {
        var text = string.Join("\n", All.Select(t => t.Subject + "\n" + t.Body + "\n" + t.Resolution));
        Assert.DoesNotContain("@", text);
        Assert.DoesNotMatch(new Regex(@"\d{5,}"), text);
        Assert.DoesNotContain("http", text);
    }

    [Fact]
    public void The_demo_clock_is_after_every_ticket_and_the_demos_are_what_they_say()
    {
        Assert.True(All.Max(t => t.CreatedAt) < SampleTickets.DemoNow);
        Assert.Equal(4, SampleTickets.Demos.Count);
        Assert.All(SampleTickets.Demos, d =>
        {
            Assert.Equal(SampleTickets.DemoNow, d.Ticket.CreatedAt);
            Assert.False(string.IsNullOrWhiteSpace(d.Note));
        });
        Assert.Subset(All.Select(t => t.GroupId!).Where(g => g is not null).ToHashSet(), SampleTickets.Demos.Where(d => d.ExpectedGroup is not null).Select(d => d.ExpectedGroup!).ToHashSet());
    }

    [Fact]
    public void The_demo_tickets_are_not_in_the_history()
    {
        var history = All.Select(t => t.Subject).ToHashSet();
        Assert.DoesNotContain(SampleTickets.Demos, d => history.Contains(d.Ticket.Subject));
    }

    [Fact]
    public void Several_groups_deliberately_share_vocabulary()
    {
        // The point of the samples: neighbours that a match on shared words alone could confuse.
        string Text(string group) => string.Join(" ", All.Where(t => t.GroupId == group).Select(t => t.Subject + " " + t.Body)).ToLowerInvariant();
        Assert.Contains("password", Text("ACC-LOOP"));
        Assert.Contains("password", Text("ACC-RESET"));
        Assert.Contains("password", Text("ACC-LOCK"));
        Assert.Contains("refund", Text("BIL-TWICE"));
        Assert.Contains("refund", Text("BIL-REFUND"));
        Assert.Contains("export", Text("REP-CSV"));
        Assert.Contains("export", Text("REP-TIMEOUT"));
    }
}
