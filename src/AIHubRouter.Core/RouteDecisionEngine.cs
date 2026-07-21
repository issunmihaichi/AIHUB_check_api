namespace AIHubRouter.Core;

public sealed record RouteDecisionResult(RouteDecision Decision, RouteState NextState);

public static class RouteDecisionEngine
{
    public static RouteDecisionResult Decide(
        RouteEvaluation evaluation,
        RouteState state,
        BalancedRoutingPolicy policy,
        DateTimeOffset now,
        long? observedCurrentGroupId = null) =>
        Decide(
            evaluation,
            state,
            policy,
            new AdaptiveRoutingContext(policy.Mode, TaskDurationCategory.Medium, null),
            now,
            observedCurrentGroupId);

    public static RouteDecisionResult Decide(
        RouteEvaluation evaluation,
        RouteState state,
        BalancedRoutingPolicy policy,
        AdaptiveRoutingContext context,
        DateTimeOffset now,
        long? observedCurrentGroupId = null)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        policy.Validate();

        var effectivePreference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
            context.CurrentIntervalSeconds,
            AdaptiveSwitchDecisionEngine.ToPreference(context.BaseMode));
        var currentGroupId = observedCurrentGroupId ?? state.CurrentGroupId;
        var current = evaluation.EligibleCandidates.FirstOrDefault(
            candidate => candidate.Group.Id == currentGroupId);
        var target = effectivePreference == AdaptivePreference.Cost
            ? SelectStrictCheapest(evaluation.EligibleCandidates)
            : evaluation.Recommended;
        if (target is null)
        {
            return Enrich(
                Result(current, null, false, RouteDecisionReason.NoCandidate,
                    new RouteState { CurrentGroupId = currentGroupId }, 0, null, now),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "没有符合条件的路由");
        }

        var premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        if (currentGroupId is null)
        {
            return Enrich(
                Switched(current, target, RouteDecisionReason.InitialRoute, state, premium, null, now),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "建立初始路由");
        }

        if (current is null)
        {
            return Enrich(
                Switched(current, target, RouteDecisionReason.CurrentRouteInvalid, state, premium, null, now),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "当前路由已不可用");
        }

        var selected = SelectAdaptiveTarget(
            current,
            evaluation.EligibleCandidates,
            context,
            effectivePreference);
        if (selected is null)
        {
            if (current.Group.Id == target.Group.Id)
            {
                return Enrich(
                    Result(current, target, false, RouteDecisionReason.AlreadyOptimal,
                        new RouteState { CurrentGroupId = current.Group.Id }, premium, 0, now),
                    context,
                    effectivePreference,
                    adaptiveDecision: null,
                    "当前路由已是最优");
            }

            selected = new AdaptiveCandidateSelection(
                target,
                EvaluateAdaptiveDecision(current, target, context));
        }

        target = selected.Candidate;
        premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        var adaptiveDecision = selected.Decision;
        var latencyImprovement = CalculateLatencyImprovement(
            current.Provider.FirstTokenLatencyMs,
            target.Provider.FirstTokenLatencyMs);

        if (adaptiveDecision.ShouldSwitch)
        {
            return Enrich(
                Switched(
                    current,
                    target,
                    MapReason(adaptiveDecision.Reason),
                    state,
                    premium,
                    latencyImprovement,
                    now),
                context,
                effectivePreference,
                adaptiveDecision,
                adaptiveDecision.Detail);
        }

        return Enrich(
            Result(
                current,
                current,
                false,
                MapReason(adaptiveDecision.Reason),
                new RouteState { CurrentGroupId = current.Group.Id },
                CalculatePremium(evaluation.MinimumMultiplier, current.EffectiveMultiplier),
                0,
                now),
            context,
            effectivePreference,
            adaptiveDecision,
            adaptiveDecision.Detail);
    }

    private static RouteDecisionResult Enrich(
        RouteDecisionResult result,
        AdaptiveRoutingContext context,
        AdaptivePreference effectivePreference,
        AdaptiveSwitchDecision? adaptiveDecision,
        string detail) =>
        result with
        {
            Decision = result.Decision with
            {
                EffectivePreference = effectivePreference,
                DurationCategory = context.DurationCategory,
                CurrentIntervalSeconds = context.CurrentIntervalSeconds,
                AdaptiveDecision = adaptiveDecision,
                Detail = detail
            }
        };

    private static RouteDecisionReason MapReason(AdaptiveDecisionReason reason) => reason switch
    {
        AdaptiveDecisionReason.AcceptedCost => RouteDecisionReason.AdaptiveCostAccepted,
        AdaptiveDecisionReason.AcceptedBalanced => RouteDecisionReason.AdaptiveBalancedAccepted,
        AdaptiveDecisionReason.AcceptedSpeed => RouteDecisionReason.AdaptiveSpeedAccepted,
        AdaptiveDecisionReason.NewPriceNotLower => RouteDecisionReason.AdaptivePriceNotLower,
        AdaptiveDecisionReason.ShortTaskProtected => RouteDecisionReason.AdaptiveShortTaskProtected,
        AdaptiveDecisionReason.RemainingWorkTooSmall => RouteDecisionReason.AdaptiveRemainingWorkTooSmall,
        AdaptiveDecisionReason.CostGuardRejected => RouteDecisionReason.AdaptiveCostRejected,
        AdaptiveDecisionReason.BalancedGuardRejected => RouteDecisionReason.AdaptiveBalancedRejected,
        AdaptiveDecisionReason.SpeedGuardRejected => RouteDecisionReason.AdaptiveSpeedRejected,
        _ => RouteDecisionReason.AdaptiveUnknownPreference
    };

    private static RouteCandidate? SelectStrictCheapest(IEnumerable<RouteCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.EffectiveMultiplier)
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
            .ThenBy(candidate => RoutingEngine.NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault();

    private static AdaptiveCandidateSelection? SelectAdaptiveTarget(
        RouteCandidate current,
        IEnumerable<RouteCandidate> candidates,
        AdaptiveRoutingContext context,
        AdaptivePreference effectivePreference)
    {
        var accepted = candidates
            .Where(candidate => candidate.Group.Id != current.Group.Id)
            .Select(candidate => new AdaptiveCandidateSelection(
                candidate,
                EvaluateAdaptiveDecision(current, candidate, context)))
            .Where(selection => selection.Decision.ShouldSwitch)
            .ToArray();

        return effectivePreference switch
        {
            AdaptivePreference.Cost => accepted
                .OrderByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id)
                .FirstOrDefault(),
            AdaptivePreference.Balanced => accepted
                .OrderByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id)
                .FirstOrDefault(),
            AdaptivePreference.Speed => accepted
                .OrderBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenByDescending(selection => selection.Candidate.Provider.OutputTokensPerSecond ?? 0)
                .ThenByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id)
                .FirstOrDefault(),
            _ => null
        };
    }

    private static AdaptiveSwitchDecision EvaluateAdaptiveDecision(
        RouteCandidate current,
        RouteCandidate candidate,
        AdaptiveRoutingContext context) =>
        AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            current.EffectiveMultiplier,
            candidate.EffectiveMultiplier,
            ToSeconds(current.Provider.FirstTokenLatencyMs),
            ToSeconds(candidate.Provider.FirstTokenLatencyMs),
            current.Provider.OutputTokensPerSecond ?? 0,
            candidate.Provider.OutputTokensPerSecond ?? 0,
            context.DurationCategory,
            AdaptiveSwitchDecisionEngine.ToPreference(context.BaseMode),
            context.CurrentIntervalSeconds));

    private sealed record AdaptiveCandidateSelection(
        RouteCandidate Candidate,
        AdaptiveSwitchDecision Decision);

    private static RouteDecisionResult Switched(
        RouteCandidate? current,
        RouteCandidate target,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        DateTimeOffset now) =>
        Result(current, target, true, reason,
            new RouteState { CurrentGroupId = target.Group.Id }, premium, latencyImprovement, now);

    private static RouteDecisionResult Result(
        RouteCandidate? current,
        RouteCandidate? target,
        bool shouldSwitch,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        DateTimeOffset now) =>
        new(
            new RouteDecision(current, target, shouldSwitch, reason, premium, latencyImprovement, now),
            state);

    private static double CalculatePremium(double? minimum, double value)
    {
        if (minimum is null || minimum <= 0)
        {
            return value <= 0 ? 0 : double.PositiveInfinity;
        }

        return Math.Max(0, (value - minimum.Value) / minimum.Value * 100);
    }

    private static double? CalculateLatencyImprovement(double? current, double? target)
    {
        if (current is not { } currentValue || target is not { } targetValue ||
            !double.IsFinite(currentValue) || !double.IsFinite(targetValue) ||
            currentValue <= 0 || targetValue < 0)
        {
            return null;
        }

        return (currentValue - targetValue) / currentValue * 100;
    }

    private static double ToSeconds(double? milliseconds) =>
        milliseconds is >= 0 && double.IsFinite(milliseconds.Value)
            ? milliseconds.Value / 1_000
            : double.PositiveInfinity;
}
