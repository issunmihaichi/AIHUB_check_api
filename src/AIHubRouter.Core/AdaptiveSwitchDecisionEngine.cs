namespace AIHubRouter.Core;

public static class AdaptiveRoutingConstants
{
    public const double InputPricePerMillion = 5.0;
    public const double OutputPricePerMillion = 30.0;
    public const double PenaltyTokens = 300_000;
    public const double PlanningTokensPerSecond = 21.8;
    public const double MinimumUsefulRemainingTokens = 1_000;
    public const double MaximumCostCompletionSeconds = 24 * 60 * 60;

    public static DurationConfiguration Duration(TaskDurationCategory category) => category switch
    {
        TaskDurationCategory.Short => new(0, 78_480, 3_600),
        TaskDurationCategory.Medium => new(78_480, 313_920, 7_200),
        TaskDurationCategory.Long => new(313_920, 1_883_520, 21_600),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}

public static class AdaptiveSwitchDecisionEngine
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(1);

    public static AdaptivePreference ToPreference(RoutingMode mode) => mode switch
    {
        RoutingMode.Economy => AdaptivePreference.Cost,
        RoutingMode.Balanced => AdaptivePreference.Balanced,
        RoutingMode.Speed => AdaptivePreference.Speed,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static RoutingMode ToRoutingMode(AdaptivePreference preference) => preference switch
    {
        AdaptivePreference.Cost => RoutingMode.Economy,
        AdaptivePreference.Balanced => RoutingMode.Balanced,
        AdaptivePreference.Speed => RoutingMode.Speed,
        _ => throw new ArgumentOutOfRangeException(nameof(preference))
    };

    public static AdaptivePreference ResolveEffectivePreference(
        double? currentIntervalSeconds,
        AdaptivePreference basePreference)
    {
        if (currentIntervalSeconds is not { } interval ||
            !double.IsFinite(interval) ||
            interval < 0)
        {
            return basePreference;
        }

        if (interval < 5)
        {
            return AdaptivePreference.Speed;
        }

        if (interval <= 15)
        {
            return basePreference;
        }

        if (interval <= 30)
        {
            return basePreference == AdaptivePreference.Speed
                ? AdaptivePreference.Balanced
                : AdaptivePreference.Cost;
        }

        return AdaptivePreference.Cost;
    }

    public static double? ResolveCurrentIntervalSeconds(
        IEnumerable<ProviderStatus> providers,
        long? currentGroupId,
        string platform,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new ArgumentException("Routing platform is required.", nameof(platform));
        }

        var matching = providers.Where(provider =>
            provider.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));
        if (currentGroupId is { } groupId)
        {
            matching = matching.Where(provider => provider.GroupId == groupId);
        }

        var lastCall = matching
            .Select(provider => provider.ResolvedLastCallEndedAt)
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .DefaultIfEmpty()
            .Max();
        if (lastCall == default)
        {
            return null;
        }

        if (lastCall > now + MaximumFutureClockSkew)
        {
            return null;
        }

        return Math.Max(0, (now - lastCall).TotalSeconds);
    }
}
