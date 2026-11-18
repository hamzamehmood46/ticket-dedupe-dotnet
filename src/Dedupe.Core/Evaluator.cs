namespace Dedupe.Core;

/// <summary>Counts for "score at or above this threshold means duplicate", over a set of ticket pairs.</summary>
public sealed record ThresholdPoint(double Threshold, int TruePositives, int FalsePositives, int FalseNegatives)
{
    /// <summary>Of the pairs called duplicates, the share that really are. 1 when nothing was called a duplicate.</summary>
    public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);

    /// <summary>Of the real duplicate pairs, the share that were found.</summary>
    public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed record PairError(string IdA, string SubjectA, string IdB, string SubjectB, double Score);

public sealed record EvaluationReport(
    int DevTickets,
    int TestTickets,
    int DevDuplicatePairs,
    int TestDuplicatePairs,
    IReadOnlyList<ThresholdPoint> DevSweep,
    IReadOnlyList<ThresholdPoint> TestSweep,
    double DuplicateThreshold,
    bool PrecisionTargetMet,
    double RelatedThreshold,
    ThresholdPoint TestAtDuplicate,
    ThresholdPoint TestAtRelated,
    int RankedQueries,
    double HitAt1,
    double HitAt3,
    double Mrr,
    IReadOnlyList<PairError> FalsePositives,
    IReadOnlyList<PairError> FalseNegatives);

/// <summary>
/// Measures how well similarity separates real duplicates from everything else, and picks the thresholds.
/// Tickets are split by duplicate group: every third group (and every third one-off ticket) is held out as a test set.
/// The word weights and both thresholds are fitted on the development half only, and the numbers that matter are
/// reported on the held-out half.
/// </summary>
public static class Evaluator
{
    public static (List<Ticket> Dev, List<Ticket> Test) Split(IReadOnlyList<Ticket> tickets)
    {
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var singles = 0;
        var dev = new List<Ticket>();
        var test = new List<Ticket>();

        foreach (var t in tickets)
        {
            int n;
            if (t.GroupId is null)
            {
                n = singles++;
            }
            else
            {
                if (!groupIndex.TryGetValue(t.GroupId, out n))
                {
                    n = groupIndex.Count;
                    groupIndex[t.GroupId] = n;
                }
            }

            (n % 3 == 2 ? test : dev).Add(t);
        }

        return (dev, test);
    }

    /// <param name="targetPrecision">Duplicate threshold: the lowest score where at most this share of "duplicates" are wrong. Merging two different problems is worse than missing a duplicate.</param>
    /// <param name="targetRecall">Related threshold: the highest score that still finds this share of real duplicates, so a helpful ticket is rarely hidden.</param>
    public static EvaluationReport Run(IReadOnlyList<Ticket> tickets, double targetPrecision = 0.95, double targetRecall = 0.90)
    {
        var (dev, test) = Split(tickets);
        if (dev.Count < 2 || test.Count < 2)
        {
            throw new ArgumentException("Need at least two tickets on each side of the split.", nameof(tickets));
        }

        var embedder = HashingEmbedder.Fit(dev.Select(DuplicateFinder.Text));
        var devVectors = Embed(dev, embedder);
        var testVectors = Embed(test, embedder);

        var devPairs = Pairs(dev, devVectors);
        var testPairs = Pairs(test, testVectors);
        var devSweep = Sweep(devPairs);
        var testSweep = Sweep(testPairs);

        var (duplicate, met) = TuneDuplicate(devSweep, targetPrecision);
        var related = Math.Min(TuneRelated(devSweep, targetRecall), duplicate);

        var (hit1, hit3, mrr, ranked) = Retrieval(test, testVectors);

        return new EvaluationReport(
            dev.Count,
            test.Count,
            devPairs.Count(p => p.Duplicate),
            testPairs.Count(p => p.Duplicate),
            devSweep,
            testSweep,
            duplicate,
            met,
            related,
            At(testPairs, duplicate),
            At(testPairs, related),
            ranked,
            hit1,
            hit3,
            mrr,
            testPairs.Where(p => !p.Duplicate && p.Score >= duplicate).OrderByDescending(p => p.Score).Take(5).Select(ToError).ToList(),
            testPairs.Where(p => p.Duplicate && p.Score < duplicate).OrderBy(p => p.Score).Take(5).Select(ToError).ToList());
    }

    private sealed record ScoredPair(Ticket A, Ticket B, double Score, bool Duplicate);

    private static Dictionary<string, float[]> Embed(IEnumerable<Ticket> tickets, IEmbedder embedder) =>
        tickets.ToDictionary(t => t.Id, t => embedder.Embed(DuplicateFinder.Text(t)), StringComparer.Ordinal);

    private static List<ScoredPair> Pairs(IReadOnlyList<Ticket> items, Dictionary<string, float[]> vectors)
    {
        var pairs = new List<ScoredPair>(items.Count * (items.Count - 1) / 2);
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];
                var duplicate = a.GroupId is not null && a.GroupId == b.GroupId;
                pairs.Add(new ScoredPair(a, b, Vectors.Cosine(vectors[a.Id], vectors[b.Id]), duplicate));
            }
        }

        return pairs;
    }

    private static ThresholdPoint At(List<ScoredPair> pairs, double threshold)
    {
        var tp = 0;
        var fp = 0;
        var fn = 0;
        foreach (var p in pairs)
        {
            var called = p.Score >= threshold;
            if (called && p.Duplicate)
            {
                tp++;
            }
            else if (called)
            {
                fp++;
            }
            else if (p.Duplicate)
            {
                fn++;
            }
        }

        return new ThresholdPoint(threshold, tp, fp, fn);
    }

    private static List<ThresholdPoint> Sweep(List<ScoredPair> pairs) =>
        Enumerable.Range(0, 101).Select(i => At(pairs, i / 100.0)).ToList();

    /// <summary>The lowest threshold whose precision meets the target (with at least one correct pair), else the best F1.</summary>
    internal static (double Threshold, bool TargetMet) TuneDuplicate(IReadOnlyList<ThresholdPoint> sweep, double targetPrecision)
    {
        var meets = sweep.FirstOrDefault(p => p.TruePositives > 0 && p.Precision >= targetPrecision);
        if (meets is not null)
        {
            return (meets.Threshold, true);
        }

        var best = sweep.OrderByDescending(p => p.F1).ThenByDescending(p => p.Threshold).First();
        return (best.Threshold, false);
    }

    /// <summary>The highest threshold that still finds the target share of real duplicates.</summary>
    internal static double TuneRelated(IReadOnlyList<ThresholdPoint> sweep, double targetRecall) =>
        sweep.LastOrDefault(p => p.Recall >= targetRecall)?.Threshold ?? 0;

    /// <summary>For each held-out ticket that has a real duplicate among the others: where does the nearest duplicate rank?</summary>
    private static (double Hit1, double Hit3, double Mrr, int Queries) Retrieval(List<Ticket> test, Dictionary<string, float[]> vectors)
    {
        var queries = 0;
        double hit1 = 0, hit3 = 0, mrr = 0;
        foreach (var q in test.Where(t => t.GroupId is not null && test.Any(o => o.Id != t.Id && o.GroupId == t.GroupId)))
        {
            var ranked = test
                .Where(o => o.Id != q.Id)
                .Select(o => (o, Score: Vectors.Cosine(vectors[q.Id], vectors[o.Id])))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .ToList();
            var rank = ranked.FindIndex(x => x.o.GroupId == q.GroupId) + 1;

            queries++;
            hit1 += rank == 1 ? 1 : 0;
            hit3 += rank <= 3 ? 1 : 0;
            mrr += 1.0 / rank;
        }

        return queries == 0 ? (0, 0, 0, 0) : (hit1 / queries, hit3 / queries, mrr / queries, queries);
    }

    private static PairError ToError(ScoredPair p) => new(p.A.Id, p.A.Subject, p.B.Id, p.B.Subject, p.Score);
}
