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
        long? observedCurrentGroupId = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(basePolicy);
        ArgumentNullException.ThrowIfNull(state);
        basePolicy.Validate();

        var currentGroupId = observedCurrentGroupId ?? state.CurrentGroupId;
        var currentInterval = AdaptiveSwitchDecisionEngine.ResolveCurrentIntervalSeconds(
            providers,
            currentGroupId,
            basePolicy.Platform,
            now);
        var basePreference = AdaptiveSwitchDecisionEngine.ToPreference(basePolicy.Mode);
        var effectivePreference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
            currentInterval,
            basePreference);
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
                currentInterval),
            now,
            observedCurrentGroupId);

        return new RouteDecisionSnapshot(evaluation, decision);
    }
}
