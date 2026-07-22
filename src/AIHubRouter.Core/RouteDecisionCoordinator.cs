namespace AIHubRouter.Core;

public sealed record RouteDecisionSnapshot(
    RouteEvaluation Evaluation,
    RouteDecisionResult Result);

public static class RouteDecisionCoordinator
{
    public static RouteDecisionSnapshot Evaluate(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> groups,
        IReadOnlyDictionary<long, double> userGroupRates,
        BalancedRoutingPolicy basePolicy,
        TaskDurationCategory durationCategory,
        RouteState state,
        DateTimeOffset now,
        long? observedCurrentGroupId = null,
        double? balancedRemainingSeconds = null,
        double? balancedDeadlineSoftSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(basePolicy);
        ArgumentNullException.ThrowIfNull(state);
        basePolicy.Validate();

        var currentGroupId = observedCurrentGroupId ?? state.CurrentGroupId;
        var currentInterval = currentGroupId is null
            ? null
            : AdaptiveSwitchDecisionEngine.ResolveCurrentIntervalSeconds(
                providers,
                currentGroupId,
                basePolicy.Platform,
                now);
        var basePreference = AdaptiveSwitchDecisionEngine.ToPreference(basePolicy.Mode);
        var effectivePreference = basePolicy.Mode switch
        {
            RoutingMode.Economy => AdaptivePreference.Cost,
            RoutingMode.Balanced when balancedRemainingSeconds is { } remaining =>
                remaining <= 0 ? AdaptivePreference.Cost : AdaptivePreference.Balanced,
            _ => AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
                currentInterval,
                basePreference)
        };
        var effectivePolicy = basePolicy with
        {
            Mode = AdaptiveSwitchDecisionEngine.ToRoutingMode(effectivePreference)
        };
        var evaluation = RoutingEngine.Evaluate(
            providers,
            groups,
            userGroupRates,
            effectivePolicy,
            now);
        var decision = RouteDecisionEngine.Decide(
            evaluation,
            state,
            effectivePolicy,
            new AdaptiveRoutingContext(
                basePolicy.Mode,
                durationCategory,
                currentInterval,
                balancedRemainingSeconds,
                balancedDeadlineSoftSeconds),
            now,
            observedCurrentGroupId);

        return new RouteDecisionSnapshot(evaluation, decision);
    }
}
