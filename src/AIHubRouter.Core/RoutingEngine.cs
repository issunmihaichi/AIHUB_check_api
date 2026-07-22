namespace AIHubRouter.Core;

public static class RoutingEngine
{
    public static RouteCandidate? SelectCheapest(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> availableGroups,
        IReadOnlyDictionary<long, double> userGroupRates,
        RoutingCriteria criteria,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(availableGroups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(criteria);

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(group => group.Id);
        var blocklist = criteria.Blocklist ?? ProviderBlocklist.Empty;

        return providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => !blocklist.IsBlocked(provider, groups[provider.GroupId!.Value]))
            .Where(provider => provider.Platform.Equals(criteria.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, criteria.MaximumStatusAge))
            .Where(provider => (provider.SuccessRate6h ?? 0) >= criteria.MinimumSuccessRate6h)
            .Select(provider =>
            {
                var group = groups[provider.GroupId!.Value];
                var hasOverride = userGroupRates.TryGetValue(group.Id, out var overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                return new RouteCandidate(provider, group, effectiveRate, hasOverride);
            })
            .Where(candidate => candidate.EffectiveMultiplier >= 0 && double.IsFinite(candidate.EffectiveMultiplier))
            .GroupBy(candidate => candidate.Group.Id)
            .Select(group => group
                .OrderBy(candidate => candidate.EffectiveMultiplier)
                .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
                .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
                .First())
            .OrderBy(candidate => candidate.EffectiveMultiplier)
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
            .ThenBy(candidate => candidate.Provider.FirstTokenLatencyMs ?? double.MaxValue)
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault();
    }

    public static RouteEvaluation Evaluate(
        IEnumerable<ProviderStatus> providers,
        IEnumerable<GroupInfo> availableGroups,
        IReadOnlyDictionary<long, double> userGroupRates,
        BalancedRoutingPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(availableGroups);
        ArgumentNullException.ThrowIfNull(userGroupRates);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var groups = availableGroups
            .Where(group => group.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var blocklist = policy.Blocklist ?? ProviderBlocklist.Empty;

        var eligible = providers
            .Where(provider => provider.Enabled && provider.Available)
            .Where(provider => provider.GroupId is > 0 && groups.ContainsKey(provider.GroupId.Value))
            .Where(provider => !blocklist.IsBlocked(provider, groups[provider.GroupId!.Value]))
            .Where(provider => provider.Platform.Equals(policy.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.PriceMultiplier >= 0 && double.IsFinite(provider.PriceMultiplier))
            .Where(provider => IsFresh(provider.CheckedAt, now, policy.MaximumStatusAge))
            .Where(provider => (provider.SuccessRate6h ?? 0) >= policy.MinimumSuccessRate6h)
            .Select(provider =>
            {
                var group = groups[provider.GroupId!.Value];
                var hasOverride = userGroupRates.TryGetValue(group.Id, out var overrideRate);
                var effectiveRate = hasOverride ? overrideRate : provider.PriceMultiplier;
                return new RouteCandidate(provider, group, effectiveRate, hasOverride);
            })
            .Where(candidate => candidate.EffectiveMultiplier >= 0 && double.IsFinite(candidate.EffectiveMultiplier))
            .GroupBy(candidate => candidate.Group.Id)
            .Select(group => group
                .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
                .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
                .ThenBy(candidate => candidate.EffectiveMultiplier)
                .ThenBy(candidate => candidate.Provider.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => candidate.Group.Id)
            .ToArray();

        if (eligible.Length == 0)
        {
            return new RouteEvaluation(null, null, eligible, new Dictionary<long, double>(), null,
                policy.PriceWeight, policy.LatencyWeight);
        }

        var measured = eligible
            .Where(candidate => IsKnownLatency(candidate.Provider.FirstTokenLatencyMs))
            .ToArray();
        var decisionPool = measured.Length > 0 ? measured : eligible;
        var minimumMultiplier = decisionPool.Min(candidate => candidate.EffectiveMultiplier);
        var cheapest = decisionPool
            .Where(candidate => NearlyEqual(candidate.EffectiveMultiplier, minimumMultiplier))
            .OrderBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
            .ThenBy(candidate => candidate.Group.Id)
            .ToArray();
        var baseline = cheapest[0];
        var scores = new Dictionary<long, double>();

        if (minimumMultiplier == 0 || measured.Length == 0 ||
            !IsKnownLatency(baseline.Provider.FirstTokenLatencyMs))
        {
            foreach (var candidate in cheapest)
            {
                scores[candidate.Group.Id] = 0;
            }

            return new RouteEvaluation(baseline, baseline, eligible, scores, minimumMultiplier,
                policy.PriceWeight, policy.LatencyWeight);
        }

        var baselineLatency = baseline.Provider.FirstTokenLatencyMs!.Value;
        foreach (var candidate in decisionPool)
        {
            var score = CalculateTradeoffScore(
                minimumMultiplier,
                baselineLatency,
                candidate,
                policy.PriceWeight,
                policy.LatencyWeight);
            scores[candidate.Group.Id] = score;
        }

        var recommended = decisionPool
            .Where(candidate => candidate.Group.Id == baseline.Group.Id || scores[candidate.Group.Id] > 1e-9)
            .OrderByDescending(candidate => scores[candidate.Group.Id])
            .ThenBy(candidate => candidate.EffectiveMultiplier)
            .ThenBy(candidate => NormalizeLatency(candidate.Provider.FirstTokenLatencyMs))
            .ThenByDescending(candidate => candidate.Provider.SuccessRate6h ?? 0)
            .ThenBy(candidate => candidate.Group.Id)
            .FirstOrDefault() ?? baseline;

        return new RouteEvaluation(recommended, baseline, eligible, scores, minimumMultiplier,
            policy.PriceWeight, policy.LatencyWeight);
    }

    internal static double NormalizeLatency(double? latency)
    {
        return latency is > 0 && double.IsFinite(latency.Value)
            ? latency.Value
            : double.MaxValue;
    }

    private static bool IsKnownLatency(double? latency) =>
        latency is > 0 && double.IsFinite(latency.Value);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-12;

    private static double CalculateTradeoffScore(
        double minimumMultiplier,
        double baselineLatency,
        RouteCandidate candidate,
        double priceWeight,
        double latencyWeight)
    {
        var pricePremiumRatio = (candidate.EffectiveMultiplier - minimumMultiplier) / minimumMultiplier;
        var speedupRatio = baselineLatency / candidate.Provider.FirstTokenLatencyMs!.Value - 1;
        var score = latencyWeight * speedupRatio - priceWeight * pricePremiumRatio;
        if (double.IsFinite(score))
        {
            return score;
        }

        return score > 0 ? double.MaxValue : double.MinValue;
    }

    private static bool IsFresh(DateTimeOffset? checkedAt, DateTimeOffset now, TimeSpan maximumAge)
    {
        if (checkedAt is null)
        {
            return false;
        }

        var age = now - checkedAt.Value;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }
}
