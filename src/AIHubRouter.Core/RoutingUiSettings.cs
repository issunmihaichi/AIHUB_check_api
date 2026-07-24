using System.Collections.Immutable;

namespace AIHubRouter.Core;

public sealed record RoutingUiSettings
{
    public int MinimumSuccessPercent { get; init; }
    public int PollingIntervalSeconds { get; init; } = 60;
    public int AccountCacheSeconds { get; init; } = 300;
    public bool AutoRoute { get; init; }
    public TaskDurationCategory DurationCategory { get; init; } = TaskDurationCategory.Medium;
    public double BalancedSoftDeadlineSeconds { get; init; } = BalancedDeadlineEngine.DefaultSoftDeadlineSeconds;
    public double BalancedExpectedOutputTokens { get; init; } = BalancedDeadlineEngine.DefaultExpectedOutputTokens;
    public bool ActiveProbeEnabled { get; init; }
    public long? ActiveProbeKeyId { get; init; }
    public string ActiveProbeModel { get; init; } = string.Empty;
    public WinFormsTheme Theme { get; init; } = WinFormsTheme.System;
    public bool SmoothRendering { get; init; } = true;
    public ImmutableArray<long> BlockedGroupIds { get; init; } = [];
    public ImmutableArray<string> BlockedNodePatterns { get; init; } = [];

    public RoutingUiSettings Normalize()
    {
        return this with
        {
            MinimumSuccessPercent = Math.Clamp(MinimumSuccessPercent, 0, 100),
            PollingIntervalSeconds = Math.Clamp(PollingIntervalSeconds, 30, 3_600),
            AccountCacheSeconds = Math.Clamp(AccountCacheSeconds, 30, 3_600),
            DurationCategory = DurationCategory is TaskDurationCategory.Short or
                TaskDurationCategory.Medium or TaskDurationCategory.Long
                ? DurationCategory
                : TaskDurationCategory.Medium,
            BalancedSoftDeadlineSeconds = NormalizeFinite(
                BalancedSoftDeadlineSeconds,
                BalancedDeadlineEngine.DefaultSoftDeadlineSeconds,
                0,
                300),
            BalancedExpectedOutputTokens = NormalizeFinite(
                BalancedExpectedOutputTokens,
                BalancedDeadlineEngine.DefaultExpectedOutputTokens,
                0,
                10_000_000),
            ActiveProbeKeyId = ActiveProbeKeyId is > 0 ? ActiveProbeKeyId : null,
            ActiveProbeModel = ActiveProbeModel?.Trim() ?? string.Empty,
            BlockedGroupIds = (BlockedGroupIds.IsDefault ? [] : BlockedGroupIds)
                .Where(groupId => groupId > 0)
                .Distinct()
                .Order()
                .ToImmutableArray(),
            BlockedNodePatterns = (BlockedNodePatterns.IsDefault ? [] : BlockedNodePatterns)
                .Select(pattern => pattern?.Trim())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => pattern!.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray()
        };
    }

    public bool Equals(RoutingUiSettings? other) => IsEquivalentTo(other);

    public override int GetHashCode()
    {
        var normalized = Normalize();
        var hash = new HashCode();
        hash.Add(normalized.MinimumSuccessPercent);
        hash.Add(normalized.PollingIntervalSeconds);
        hash.Add(normalized.AccountCacheSeconds);
        hash.Add(normalized.AutoRoute);
        hash.Add(normalized.DurationCategory);
        hash.Add(normalized.BalancedSoftDeadlineSeconds);
        hash.Add(normalized.BalancedExpectedOutputTokens);
        hash.Add(normalized.ActiveProbeEnabled);
        hash.Add(normalized.ActiveProbeKeyId);
        hash.Add(normalized.ActiveProbeModel, StringComparer.Ordinal);
        hash.Add(normalized.Theme);
        hash.Add(normalized.SmoothRendering);

        foreach (var groupId in normalized.BlockedGroupIds)
        {
            hash.Add(groupId);
        }

        foreach (var pattern in normalized.BlockedNodePatterns)
        {
            hash.Add(pattern, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public bool IsEquivalentTo(RoutingUiSettings? other)
    {
        if (other is null)
        {
            return false;
        }

        var left = Normalize();
        var right = other.Normalize();

        return left.MinimumSuccessPercent == right.MinimumSuccessPercent &&
            left.PollingIntervalSeconds == right.PollingIntervalSeconds &&
            left.AccountCacheSeconds == right.AccountCacheSeconds &&
            left.AutoRoute == right.AutoRoute &&
            left.DurationCategory == right.DurationCategory &&
            left.BalancedSoftDeadlineSeconds == right.BalancedSoftDeadlineSeconds &&
            left.BalancedExpectedOutputTokens == right.BalancedExpectedOutputTokens &&
            left.ActiveProbeEnabled == right.ActiveProbeEnabled &&
            left.ActiveProbeKeyId == right.ActiveProbeKeyId &&
            string.Equals(left.ActiveProbeModel, right.ActiveProbeModel, StringComparison.Ordinal) &&
            left.Theme == right.Theme &&
            left.SmoothRendering == right.SmoothRendering &&
            left.BlockedGroupIds.SequenceEqual(right.BlockedGroupIds) &&
            left.BlockedNodePatterns.SequenceEqual(right.BlockedNodePatterns, StringComparer.Ordinal);
    }

    private static double NormalizeFinite(double value, double defaultValue, double minimum, double maximum) =>
        Math.Clamp(double.IsFinite(value) ? value : defaultValue, minimum, maximum);
}
