using Dedupe.Core;

namespace Dedupe.Web;

/// <summary>The finder and the evaluation, built once from the sample tickets. The browser uses the exact in-memory index and the stand-in embedder.</summary>
public sealed class AppState
{
    private Task<DuplicateFinder>? _finder;
    private EvaluationReport? _report;

    public Task<DuplicateFinder> GetFinderAsync() => _finder ??= DuplicateFinder.CreateAsync(SampleTickets.All);

    public EvaluationReport Report => _report ??= Evaluator.Run(SampleTickets.All);
}
