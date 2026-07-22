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

        var basePreference = AdaptiveSwitchDecisionEngine.ToPreference(context.BaseMode);
        var effectivePreference = context.BaseMode switch
        {
            RoutingMode.Economy => AdaptivePreference.Cost,
            RoutingMode.Balanced when context.BalancedRemainingSeconds is { } balancedRemaining =>
                balancedRemaining <= 0 ? AdaptivePreference.Cost : AdaptivePreference.Balanced,
            _ => AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
                context.CurrentIntervalSeconds,
                basePreference)
        };
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

        if (context.BaseMode == RoutingMode.Balanced &&
            context.BalancedRemainingSeconds is { } remainingSeconds)
        {
            if (remainingSeconds <= 0)
            {
                return DecideBalancedCountdownExpired(
                    evaluation,
                    current,
                    target,
                    state,
                    context,
                    now);
            }

            var deadlineDecision = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
                current,
                evaluation.EligibleCandidates,
                BalancedDeadlineEngine.EstimateOutputTokens(remainingSeconds),
                context.CurrentIntervalSeconds,
                DeadlineSoftSeconds: context.BalancedDeadlineSoftSeconds ??
                    BalancedDeadlineEngine.DefaultSoftDeadlineSeconds));
            return ApplyBalancedDeadlineDecision(
                evaluation,
                state,
                context,
                effectivePreference,
                current,
                target,
                deadlineDecision,
                now);
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

        if (effectivePreference == AdaptivePreference.Cost)
        {
            return DecideStrictEconomy(
                evaluation,
                state,
                context,
                current,
                target,
                effectivePreference,
                now);
        }

        var search = SelectAdaptiveTarget(
            current,
            evaluation.EligibleCandidates,
            context,
            effectivePreference);
        var rankings = BuildAdaptiveRankings(search);
        var selected = search.Selected;
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
                    "当前路由已是最优",
                    rankings);
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
                adaptiveDecision.Detail,
                rankings);
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
            adaptiveDecision.Detail,
            rankings);
    }

    private static RouteDecisionResult DecideStrictEconomy(
        RouteEvaluation evaluation,
        RouteState state,
        AdaptiveRoutingContext context,
        RouteCandidate? current,
        RouteCandidate target,
        AdaptivePreference effectivePreference,
        DateTimeOffset now)
    {
        var premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        var same = current is not null && current.Group.Id == target.Group.Id;
        var result = same
            ? Result(current, current, false, RouteDecisionReason.AlreadyOptimal,
                new RouteState { CurrentGroupId = current!.Group.Id }, premium, 0, now)
            : Switched(current, target, RouteDecisionReason.AdaptiveCostAccepted,
                state, premium, CalculateLatencyImprovement(
                    current?.Provider.FirstTokenLatencyMs,
                    target.Provider.FirstTokenLatencyMs), now);
        return Enrich(
            result,
            context,
            effectivePreference,
            adaptiveDecision: null,
            same ? "Economy kept the current lowest-cost route." : "Economy selected the strict lowest-cost route.");
    }

    private static RouteDecisionResult DecideBalancedCountdownExpired(
        RouteEvaluation evaluation,
        RouteCandidate? current,
        RouteCandidate target,
        RouteState state,
        AdaptiveRoutingContext context,
        DateTimeOffset now)
    {
        var same = current is not null && current.Group.Id == target.Group.Id;
        var premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        var result = same
            ? Result(current, current, false, RouteDecisionReason.BalancedCountdownExpired,
                new RouteState { CurrentGroupId = current!.Group.Id }, premium, 0, now)
            : Switched(current, target, RouteDecisionReason.BalancedCountdownExpired,
                state, premium, CalculateLatencyImprovement(
                    current?.Provider.FirstTokenLatencyMs,
                    target.Provider.FirstTokenLatencyMs), now);
        return Enrich(
            result,
            context,
            AdaptivePreference.Cost,
            adaptiveDecision: null,
            "Balanced countdown expired; Economy selected the strict lowest-cost route.");
    }

    private static RouteDecisionResult ApplyBalancedDeadlineDecision(
        RouteEvaluation evaluation,
        RouteState state,
        AdaptiveRoutingContext context,
        AdaptivePreference effectivePreference,
        RouteCandidate? current,
        RouteCandidate fallbackTarget,
        BalancedDeadlineDecision deadlineDecision,
        DateTimeOffset now)
    {
        var target = deadlineDecision.Target ?? current ?? fallbackTarget;
        var premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        var reason = deadlineDecision.Reason switch
        {
            BalancedDeadlineDecisionReason.ColdStart => RouteDecisionReason.BalancedDeadlineColdStart,
            BalancedDeadlineDecisionReason.CurrentWithinDeadline =>
                RouteDecisionReason.BalancedDeadlineCurrentWithinDeadline,
            BalancedDeadlineDecisionReason.SwitchedAfterDeadline => RouteDecisionReason.BalancedDeadlineSwitched,
            _ => RouteDecisionReason.BalancedDeadlineNoFeasibleCandidate
        };
        var same = current is not null && current.Group.Id == target.Group.Id;
        var shouldSwitch = deadlineDecision.ShouldSwitch && !same;
        var result = shouldSwitch
            ? Switched(current, target, reason, state, premium, CalculateLatencyImprovement(
                current?.Provider.FirstTokenLatencyMs,
                target.Provider.FirstTokenLatencyMs), now)
            : Result(current, current ?? target, false, reason,
                new RouteState { CurrentGroupId = target.Group.Id }, premium, 0, now);
        return Enrich(
            result,
            context,
            effectivePreference,
            adaptiveDecision: null,
            deadlineDecision.Detail,
            deadlineDecision: deadlineDecision);
    }

    private static RouteDecisionResult Enrich(
        RouteDecisionResult result,
        AdaptiveRoutingContext context,
        AdaptivePreference effectivePreference,
        AdaptiveSwitchDecision? adaptiveDecision,
        string detail,
        IReadOnlyList<AdaptiveCandidateRanking>? adaptiveRankings = null,
        BalancedDeadlineDecision? deadlineDecision = null) =>
        result with
        {
            Decision = result.Decision with
            {
                EffectivePreference = effectivePreference,
                DurationCategory = context.DurationCategory,
                CurrentIntervalSeconds = context.CurrentIntervalSeconds,
                AdaptiveDecision = adaptiveDecision,
                BalancedDeadlineDecision = deadlineDecision,
                AdaptiveRankings = adaptiveRankings ?? result.Decision.AdaptiveRankings,
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

    private static AdaptiveCandidateSearch SelectAdaptiveTarget(
        RouteCandidate current,
        IEnumerable<RouteCandidate> candidates,
        AdaptiveRoutingContext context,
        AdaptivePreference effectivePreference)
    {
        var evaluated = candidates
            .Where(candidate => candidate.Group.Id != current.Group.Id)
            .Select(candidate => new AdaptiveCandidateSelection(
                candidate,
                EvaluateAdaptiveDecision(current, candidate, context)))
            .ToArray();
        var accepted = OrderAccepted(evaluated, effectivePreference).ToArray();

        return new AdaptiveCandidateSearch(evaluated, accepted, accepted.FirstOrDefault());
    }

    private static IEnumerable<AdaptiveCandidateSelection> OrderAccepted(
        IEnumerable<AdaptiveCandidateSelection> evaluated,
        AdaptivePreference effectivePreference)
    {
        var accepted = evaluated.Where(selection => selection.Decision.ShouldSwitch);
        return effectivePreference switch
        {
            AdaptivePreference.Cost => accepted
                .OrderByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id),
            AdaptivePreference.Balanced => accepted
                .OrderByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id),
            AdaptivePreference.Speed => accepted
                .OrderBy(selection => selection.Decision.NewCompletionSeconds)
                .ThenByDescending(selection => selection.Candidate.Provider.OutputTokensPerSecond ?? 0)
                .ThenByDescending(selection => selection.Decision.NetSavingUsd)
                .ThenBy(selection => selection.Candidate.EffectiveMultiplier)
                .ThenBy(selection => selection.Candidate.Group.Id),
            _ => []
        };
    }

    private static IReadOnlyList<AdaptiveCandidateRanking> BuildAdaptiveRankings(
        AdaptiveCandidateSearch search)
    {
        var rankByGroup = search.Accepted
            .Select((selection, index) => (selection.Candidate.Group.Id, Rank: index + 1))
            .ToDictionary(entry => entry.Id, entry => entry.Rank);

        return search.Evaluated
            .Select(selection => new AdaptiveCandidateRanking(
                selection.Candidate.Provider.Id,
                selection.Candidate.Group.Id,
                rankByGroup.TryGetValue(selection.Candidate.Group.Id, out var rank) ? rank : null,
                selection.Decision.ShouldSwitch,
                selection.Decision.Reason,
                selection.Decision.NetSavingUsd,
                selection.Decision.NewCompletionSeconds))
            .OrderBy(ranking => ranking.Rank ?? int.MaxValue)
            .ThenBy(ranking => ranking.GroupId)
            .ToArray();
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

    private sealed record AdaptiveCandidateSearch(
        IReadOnlyList<AdaptiveCandidateSelection> Evaluated,
        IReadOnlyList<AdaptiveCandidateSelection> Accepted,
        AdaptiveCandidateSelection? Selected);

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
