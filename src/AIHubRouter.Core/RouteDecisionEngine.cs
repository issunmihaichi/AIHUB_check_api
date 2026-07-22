namespace AIHubRouter.Core;

public sealed record RouteDecisionResult(RouteDecision Decision, RouteState NextState);

public static class RouteDecisionEngine
{
    public static readonly TimeSpan MinimumPolicySwitchDwell = TimeSpan.FromSeconds(30);
    public const int MinimumCompletedPolicyEvaluations = 6;
    public const int MinimumStablePolicyObservations = 2;

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
                    state with { CurrentGroupId = currentGroupId }, 0, null, now),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "没有符合条件的路由");
        }

        if (currentGroupId is not null && current is null)
        {
            var recoveryPremium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
            return Enrich(
                Switched(current, target, RouteDecisionReason.CurrentRouteInvalid, state, recoveryPremium, null, now,
                    RouteSwitchClass.ForcedRecovery),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "Current route is no longer eligible; recovered immediately.");
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
                context.BalancedExpectedOutputTokens ?? 0,
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
                Switched(current, target, RouteDecisionReason.InitialRoute, state, premium, null, now,
                    RouteSwitchClass.Initial),
                context,
                effectivePreference,
                adaptiveDecision: null,
                "建立初始路由");
        }

        if (current is null)
        {
            return Enrich(
                Switched(current, target, RouteDecisionReason.CurrentRouteInvalid, state, premium, null, now,
                    RouteSwitchClass.ForcedRecovery),
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
                        ObservePolicyEvaluation(state, current.Group.Id, null), premium, 0, now),
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
            var proposed = Enrich(
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
            return ApplyPolicyHysteresis(proposed, state, current, now);
        }

        return Enrich(
            Result(
                current,
                current,
                false,
                MapReason(adaptiveDecision.Reason),
                ObservePolicyEvaluation(state, current.Group.Id, null),
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
                ObservePolicyEvaluation(state, current!.Group.Id, null), premium, 0, now)
            : Switched(current, target, RouteDecisionReason.AdaptiveCostAccepted,
                state, premium, CalculateLatencyImprovement(
                    current?.Provider.FirstTokenLatencyMs,
                    target.Provider.FirstTokenLatencyMs), now);
        var enriched = Enrich(
            result,
            context,
            effectivePreference,
            adaptiveDecision: null,
            same ? "Economy kept the current lowest-cost route." : "Economy selected the strict lowest-cost route.");
        if (same || current is null)
        {
            return enriched;
        }

        return ApplyPolicyHysteresis(enriched, state, current, now);
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
                ObservePolicyEvaluation(state, current!.Group.Id, null), premium, 0, now)
            : Switched(current, target, RouteDecisionReason.BalancedCountdownExpired,
                state, premium, CalculateLatencyImprovement(
                    current?.Provider.FirstTokenLatencyMs,
                    target.Provider.FirstTokenLatencyMs), now,
                current is null ? RouteSwitchClass.Initial : RouteSwitchClass.Policy);
        var enriched = Enrich(
            result,
            context,
            AdaptivePreference.Cost,
            adaptiveDecision: null,
            "Balanced countdown expired; Economy selected the strict lowest-cost route.");
        return same || current is null ? enriched : ApplyPolicyHysteresis(enriched, state, current, now);
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
            BalancedDeadlineDecisionReason.FastestFallback => RouteDecisionReason.BalancedDeadlineFastestFallback,
            _ => RouteDecisionReason.BalancedDeadlineNoFeasibleCandidate
        };
        var same = current is not null && current.Group.Id == target.Group.Id;
        var shouldSwitch = deadlineDecision.ShouldSwitch && !same;
        var result = shouldSwitch
            ? Switched(current, target, reason, state, premium, CalculateLatencyImprovement(
                current?.Provider.FirstTokenLatencyMs,
                target.Provider.FirstTokenLatencyMs), now,
                current is null ? RouteSwitchClass.Initial : RouteSwitchClass.Policy)
            : Result(current, current ?? target, false, reason,
                current is null
                    ? state with { CurrentGroupId = target.Group.Id }
                    : ObservePolicyEvaluation(state, current.Group.Id, null),
                premium, 0, now);
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
        AdaptiveDecisionReason.InsufficientPerformanceEvidence =>
            RouteDecisionReason.AdaptiveSpeedInsufficientEvidence,
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
            ToSeconds(current.Provider.FirstTokenLatencyP90Ms ?? current.Provider.FirstTokenLatencyMs),
            ToSeconds(candidate.Provider.FirstTokenLatencyP90Ms ?? candidate.Provider.FirstTokenLatencyMs),
            current.Provider.OutputTokensPerSecondP25 ?? current.Provider.OutputTokensPerSecond ?? 0,
            candidate.Provider.OutputTokensPerSecondP25 ?? candidate.Provider.OutputTokensPerSecond ?? 0,
            context.DurationCategory,
            AdaptiveSwitchDecisionEngine.ToPreference(context.BaseMode),
            context.CurrentIntervalSeconds,
            current.Provider.PerformanceSampleCount,
            candidate.Provider.PerformanceSampleCount));

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
        DateTimeOffset now,
        RouteSwitchClass switchClass = RouteSwitchClass.Policy) =>
        Result(current, target, true, reason,
            StateAfterSwitch(state, target.Group.Id, now, switchClass), premium, latencyImprovement, now,
            switchClass);

    private static RouteDecisionResult Result(
        RouteCandidate? current,
        RouteCandidate? target,
        bool shouldSwitch,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        DateTimeOffset now,
        RouteSwitchClass switchClass = RouteSwitchClass.None) =>
        new(
            new RouteDecision(current, target, shouldSwitch, reason, premium, latencyImprovement, now)
            {
                SwitchClass = switchClass
            },
            state);

    private static RouteDecisionResult ApplyPolicyHysteresis(
        RouteDecisionResult proposed,
        RouteState previousState,
        RouteCandidate current,
        DateTimeOffset now)
    {
        if (!proposed.Decision.ShouldSwitch || proposed.Decision.Target is not { } target)
        {
            return proposed;
        }

        // Existing route-state files predate hysteresis. Allow one migration switch, then persist guards.
        if (previousState.LastPolicySwitchAt is null)
        {
            return proposed;
        }

        var nextState = ObservePolicyEvaluation(previousState, current.Group.Id, target.Group.Id);
        if (now - previousState.LastPolicySwitchAt < MinimumPolicySwitchDwell)
        {
            return RejectPolicySwitch(proposed, current, nextState,
                RouteDecisionReason.PolicySwitchCoolingDown,
                "Policy switch is waiting for the minimum dwell period.");
        }

        if (previousState.CompletedPolicyEvaluationsSinceLastSwitch < MinimumCompletedPolicyEvaluations)
        {
            return RejectPolicySwitch(proposed, current, nextState,
                RouteDecisionReason.PolicySwitchAwaitingEvaluations,
                "Policy switch is waiting for completed routing evaluations.");
        }

        if (previousState.PendingPolicyTargetGroupId != target.Group.Id ||
            previousState.PendingPolicyTargetObservations < MinimumStablePolicyObservations)
        {
            return RejectPolicySwitch(proposed, current, nextState,
                RouteDecisionReason.PolicyCandidateNotStable,
                "Policy candidate requires another stable observation.");
        }

        return proposed;
    }

    private static RouteDecisionResult RejectPolicySwitch(
        RouteDecisionResult proposed,
        RouteCandidate current,
        RouteState nextState,
        RouteDecisionReason reason,
        string detail) =>
        proposed with
        {
            Decision = proposed.Decision with
            {
                Target = current,
                ShouldSwitch = false,
                Reason = reason,
                PricePremiumPercent = 0,
                LatencyImprovementPercent = 0,
                SwitchClass = RouteSwitchClass.None,
                Detail = detail
            },
            NextState = nextState
        };

    private static RouteState StateAfterSwitch(
        RouteState state,
        long targetGroupId,
        DateTimeOffset now,
        RouteSwitchClass switchClass) =>
        switchClass is RouteSwitchClass.Policy or RouteSwitchClass.ForcedRecovery
            ? state with
            {
                CurrentGroupId = targetGroupId,
                LastPolicySwitchAt = now,
                CompletedPolicyEvaluationsSinceLastSwitch = 0,
                PendingPolicyTargetGroupId = null,
                PendingPolicyTargetObservations = 0
            }
            : state with { CurrentGroupId = targetGroupId };

    private static RouteState ObservePolicyEvaluation(
        RouteState state,
        long currentGroupId,
        long? preferredTargetGroupId)
    {
        var sameTarget = preferredTargetGroupId is { } target &&
            target == state.PendingPolicyTargetGroupId;
        return state with
        {
            CurrentGroupId = currentGroupId,
            CompletedPolicyEvaluationsSinceLastSwitch = SaturatingIncrement(
                Math.Max(0, state.CompletedPolicyEvaluationsSinceLastSwitch)),
            PendingPolicyTargetGroupId = preferredTargetGroupId,
            PendingPolicyTargetObservations = preferredTargetGroupId is null
                ? 0
                : sameTarget
                    ? SaturatingIncrement(Math.Max(0, state.PendingPolicyTargetObservations))
                    : 1
        };
    }

    private static int SaturatingIncrement(int value) => value == int.MaxValue ? value : value + 1;

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
