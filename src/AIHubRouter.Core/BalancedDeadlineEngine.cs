namespace AIHubRouter.Core;

public enum BalancedDeadlineDecisionReason
{
    CurrentWithinDeadline,
    ColdStart,
    SwitchedAfterDeadline,
    FastestFallback,
    NoFeasibleCandidate
}

public sealed record BalancedDeadlineRequest(
    RouteCandidate? Current,
    IReadOnlyList<RouteCandidate> Candidates,
    double ExpectedOutputTokens,
    double? CurrentIntervalSeconds,
    double DeadlineSeconds = BalancedDeadlineEngine.DefaultDeadlineSeconds,
    double DeadlineSoftSeconds = BalancedDeadlineEngine.DefaultSoftDeadlineSeconds);

public sealed record BalancedDeadlineDecision(
    bool ShouldSwitch,
    RouteCandidate? Target,
    BalancedDeadlineDecisionReason Reason,
    bool IsColdStart,
    double OutputTokens,
    double? CurrentCompletionSeconds,
    double? TargetCompletionSeconds,
    double TargetCostUsd,
    string Detail);

public static class BalancedDeadlineEngine
{
    public const double DefaultDeadlineSeconds = 26.73;
    public const double DefaultSoftDeadlineSeconds = 5;
    public const double ColdStartThresholdSeconds = 30;
    public const double ContextMissPenaltyUsdPerMultiplier = 2.0;

    public static double CalculateCompletionSeconds(
        RouteCandidate candidate,
        double outputTokens)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!double.IsFinite(outputTokens) || outputTokens < 0)
        {
            return double.PositiveInfinity;
        }

        var latencyMs = candidate.Provider.FirstTokenLatencyP90Ms ?? candidate.Provider.FirstTokenLatencyMs;
        var ttftSeconds = latencyMs is { } latency &&
            double.IsFinite(latency) && latency >= 0
                ? latency / 1_000
                : double.PositiveInfinity;
        var generationSpeed = candidate.Provider.OutputTokensPerSecondP25 ??
            candidate.Provider.OutputTokensPerSecond;
        if (!double.IsFinite(ttftSeconds) ||
            generationSpeed is not { } speed ||
            !double.IsFinite(speed) ||
            speed <= 0)
        {
            return double.PositiveInfinity;
        }

        var completion = ttftSeconds + outputTokens / speed;
        return double.IsFinite(completion) ? completion : double.PositiveInfinity;
    }

    public static double CalculateSwitchCost(
        RouteCandidate candidate,
        double outputTokens)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!double.IsFinite(outputTokens) || outputTokens < 0 ||
            !double.IsFinite(candidate.EffectiveMultiplier) || candidate.EffectiveMultiplier < 0)
        {
            return double.PositiveInfinity;
        }

        return candidate.EffectiveMultiplier *
            (AdaptiveRoutingConstants.OutputPricePerMillion * outputTokens / 1_000_000 +
             ContextMissPenaltyUsdPerMultiplier);
    }

    public static BalancedDeadlineDecision Decide(BalancedDeadlineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);

        var outputTokens = double.IsFinite(request.ExpectedOutputTokens)
            ? Math.Max(0, request.ExpectedOutputTokens)
            : 0;
        var deadlineSeconds = request.DeadlineSeconds;
        if (!double.IsFinite(deadlineSeconds) || deadlineSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.DeadlineSeconds));
        }

        var softSeconds = double.IsFinite(request.DeadlineSoftSeconds)
            ? Math.Max(0, request.DeadlineSoftSeconds)
            : 0;
        var effectiveDeadlineSeconds = deadlineSeconds + softSeconds;
        if (!double.IsFinite(effectiveDeadlineSeconds))
        {
            effectiveDeadlineSeconds = deadlineSeconds;
        }

        var candidates = request.Candidates
            .Where(candidate => candidate is not null)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new BalancedDeadlineDecision(
                false,
                request.Current,
                BalancedDeadlineDecisionReason.NoFeasibleCandidate,
                IsColdStart(request.CurrentIntervalSeconds, request.Current),
                outputTokens,
                request.Current is null ? null : double.PositiveInfinity,
                null,
                double.PositiveInfinity,
                "No eligible candidate.");
        }

        var currentCompletion = request.Current is { } current
            ? CalculateCompletionSeconds(current, outputTokens)
            : (double?)null;
        var isColdStart = IsColdStart(request.CurrentIntervalSeconds, request.Current);
        if (!isColdStart && request.Current is { } existing &&
            currentCompletion is { } completion && completion <= deadlineSeconds)
        {
            return new BalancedDeadlineDecision(
                false,
                existing,
                BalancedDeadlineDecisionReason.CurrentWithinDeadline,
                false,
                outputTokens,
                completion,
                completion,
                CalculateSwitchCost(existing, outputTokens),
                "Current node meets the deadline.");
        }

        var alternatives = candidates
            .Where(candidate => request.Current is null || candidate.Group.Id != request.Current.Group.Id)
            .ToArray();
        var feasible = candidates
            .Where(candidate => CalculateCompletionSeconds(candidate, outputTokens) <= effectiveDeadlineSeconds)
            .ToArray();
        var target = isColdStart
            ? SelectCheapestFeasible(feasible, outputTokens)
            : SelectLowestCostFeasible(feasible, request.Current, outputTokens);

        var usedFastestFallback = target is null;
        if (usedFastestFallback)
        {
            target = alternatives
                .OrderBy(candidate => CalculateCompletionSeconds(candidate, outputTokens))
                .ThenBy(candidate => CalculateSwitchCost(candidate, outputTokens))
                .ThenBy(candidate => candidate.EffectiveMultiplier)
                .ThenBy(candidate => candidate.Group.Id)
                .FirstOrDefault();
        }

        if (target is null)
        {
            return new BalancedDeadlineDecision(
                false,
                request.Current,
                BalancedDeadlineDecisionReason.NoFeasibleCandidate,
                isColdStart,
                outputTokens,
                currentCompletion,
                currentCompletion,
                request.Current is { } retained ? CalculateSwitchCost(retained, outputTokens) : double.PositiveInfinity,
                "No candidate can meet the deadline.");
        }

        var switched = request.Current is null || target.Group.Id != request.Current.Group.Id;
        return new BalancedDeadlineDecision(
            switched,
            switched ? target : request.Current,
            usedFastestFallback
                ? BalancedDeadlineDecisionReason.FastestFallback
                : isColdStart
                    ? BalancedDeadlineDecisionReason.ColdStart
                    : BalancedDeadlineDecisionReason.SwitchedAfterDeadline,
            isColdStart,
            outputTokens,
            currentCompletion,
            CalculateCompletionSeconds(target, outputTokens),
            CalculateSwitchCost(target, outputTokens),
            usedFastestFallback
                ? $"No node can meet {effectiveDeadlineSeconds:0.##}s; selected the fastest alternative."
                : isColdStart
                    ? "Cold start selected the cheapest feasible node."
                    : $"Current node exceeded {deadlineSeconds:0.##}s; selected the lowest-cost node within {effectiveDeadlineSeconds:0.##}s.");
    }

    private static RouteCandidate? SelectCheapestFeasible(
        IEnumerable<RouteCandidate> candidates,
        double outputTokens) =>
        candidates
            .OrderBy(candidate => candidate.EffectiveMultiplier)
            .ThenBy(candidate => CalculateCompletionSeconds(candidate, outputTokens))
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault();

    private static RouteCandidate? SelectLowestCostFeasible(
        IEnumerable<RouteCandidate> candidates,
        RouteCandidate? current,
        double outputTokens) =>
        candidates
            .Where(candidate => current is null || candidate.Group.Id != current.Group.Id)
            .OrderBy(candidate => CalculateSwitchCost(candidate, outputTokens))
            .ThenBy(candidate => candidate.EffectiveMultiplier)
            .ThenBy(candidate => CalculateCompletionSeconds(candidate, outputTokens))
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault();

    private static bool IsColdStart(double? intervalSeconds, RouteCandidate? current) =>
        current is null ||
        intervalSeconds is not { } interval ||
        !double.IsFinite(interval) ||
        interval > ColdStartThresholdSeconds;
}
