using System.Globalization;

namespace AIHubRouter.Core;

public sealed record AdaptiveSwitchRequest(
    double OldMultiplier,
    double NewMultiplier,
    double OldTtftSeconds,
    double NewTtftSeconds,
    double OldGenerationSpeed,
    double NewGenerationSpeed,
    TaskDurationCategory DurationCategory,
    AdaptivePreference BasePreference,
    double? CurrentIntervalSeconds,
    int? OldPerformanceSampleCount = null,
    int? NewPerformanceSampleCount = null);

public enum AdaptiveDecisionReason
{
    AcceptedCost,
    AcceptedBalanced,
    AcceptedSpeed,
    NewPriceNotLower,
    ShortTaskProtected,
    RemainingWorkTooSmall,
    CostGuardRejected,
    BalancedGuardRejected,
    SpeedGuardRejected,
    InsufficientPerformanceEvidence,
    UnknownPreference
}

public sealed record AdaptiveSwitchDecision(
    bool ShouldSwitch,
    AdaptiveDecisionReason Reason,
    AdaptivePreference EffectivePreference,
    double RemainingTokens,
    double PenaltyUsd,
    double NetSavingUsd,
    double OldCompletionSeconds,
    double NewCompletionSeconds,
    double DeltaSeconds,
    string Detail);

public static class AdaptiveRoutingConstants
{
    public const double InputPricePerMillion = 5.0;
    public const double OutputPricePerMillion = 30.0;
    public const double PenaltyTokens = 300_000;
    public const double PlanningTokensPerSecond = 43.6;
    public const double MinimumUsefulRemainingTokens = 1_000;
    public const double MaximumCostCompletionSeconds = 24 * 60 * 60;
    public const int MinimumSpeedPerformanceSamples = 20;

    public static DurationConfiguration Duration(TaskDurationCategory category) => category switch
    {
        TaskDurationCategory.Short => new(0, 156_960, 3_600),
        TaskDurationCategory.Medium => new(156_960, 627_840, 7_200),
        TaskDurationCategory.Long => new(627_840, 3_767_040, 21_600),
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
        if (basePreference == AdaptivePreference.Cost)
        {
            return AdaptivePreference.Cost;
        }

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

    public static double CalculatePenalty(double newMultiplier) =>
        AdaptiveRoutingConstants.PenaltyTokens * newMultiplier *
        AdaptiveRoutingConstants.InputPricePerMillion / 1_000_000;

    public static double CalculateCompletionTime(
        double ttftSeconds,
        double generationSpeed,
        double remainingTokens)
    {
        if (!double.IsFinite(ttftSeconds) || ttftSeconds < 0 ||
            !double.IsFinite(generationSpeed) || generationSpeed <= 0 ||
            !double.IsFinite(remainingTokens) || remainingTokens < 0)
        {
            return double.PositiveInfinity;
        }

        var completion = ttftSeconds + remainingTokens / generationSpeed;
        return double.IsFinite(completion) ? completion : double.PositiveInfinity;
    }

    public static double CalculateNetSaving(
        double oldMultiplier,
        double newMultiplier,
        double remainingTokens)
    {
        if (newMultiplier >= oldMultiplier)
        {
            return double.NegativeInfinity;
        }

        var outputSaving = remainingTokens * (oldMultiplier - newMultiplier) *
            AdaptiveRoutingConstants.OutputPricePerMillion / 1_000_000;
        return outputSaving - CalculatePenalty(newMultiplier);
    }

    public static AdaptiveSwitchDecision Decide(AdaptiveSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effectivePreference = ResolveEffectivePreference(
            request.CurrentIntervalSeconds,
            request.BasePreference);
        var penalty = CalculatePenalty(request.NewMultiplier);

        if (request.NewMultiplier >= request.OldMultiplier &&
            effectivePreference != AdaptivePreference.Speed)
        {
            return Rejected(
                AdaptiveDecisionReason.NewPriceNotLower,
                effectivePreference,
                penalty,
                "新价格未降低");
        }

        if (request.DurationCategory == TaskDurationCategory.Short &&
            effectivePreference != AdaptivePreference.Cost)
        {
            return Rejected(
                AdaptiveDecisionReason.ShortTaskProtected,
                effectivePreference,
                penalty,
                "短任务且非空闲状态，避免切换风险");
        }

        var config = AdaptiveRoutingConstants.Duration(request.DurationCategory);
        var remainingTokens = effectivePreference == AdaptivePreference.Cost
            ? config.MaximumRemainingTokens
            : config.MinimumRemainingTokens;
        if (remainingTokens <= AdaptiveRoutingConstants.MinimumUsefulRemainingTokens)
        {
            return Rejected(
                AdaptiveDecisionReason.RemainingWorkTooSmall,
                effectivePreference,
                penalty,
                "剩余工作量极少，不值得切换",
                remainingTokens);
        }

        var netSaving = CalculateNetSaving(
            request.OldMultiplier,
            request.NewMultiplier,
            remainingTokens);
        var oldCompletion = CalculateCompletionTime(
            request.OldTtftSeconds,
            request.OldGenerationSpeed,
            remainingTokens);
        var newCompletion = CalculateCompletionTime(
            request.NewTtftSeconds,
            request.NewGenerationSpeed,
            remainingTokens);
        var delta = newCompletion - oldCompletion;

        return effectivePreference switch
        {
            AdaptivePreference.Cost => DecideCost(
                remainingTokens, penalty, netSaving, oldCompletion, newCompletion, delta),
            AdaptivePreference.Balanced => DecideBalanced(
                request, config, remainingTokens, penalty, netSaving, oldCompletion, newCompletion, delta),
            AdaptivePreference.Speed => DecideSpeed(
                request, remainingTokens, penalty, netSaving, oldCompletion, newCompletion, delta),
            _ => Decision(
                false,
                AdaptiveDecisionReason.UnknownPreference,
                effectivePreference,
                remainingTokens,
                penalty,
                netSaving,
                oldCompletion,
                newCompletion,
                delta,
                "未知偏好")
        };
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

    private static AdaptiveSwitchDecision DecideCost(
        double remainingTokens,
        double penalty,
        double netSaving,
        double oldCompletion,
        double newCompletion,
        double delta)
    {
        var accepted = netSaving > 0 &&
            newCompletion < AdaptiveRoutingConstants.MaximumCostCompletionSeconds;
        return Decision(
            accepted,
            accepted ? AdaptiveDecisionReason.AcceptedCost : AdaptiveDecisionReason.CostGuardRejected,
            AdaptivePreference.Cost,
            remainingTokens,
            penalty,
            netSaving,
            oldCompletion,
            newCompletion,
            delta,
            accepted
                ? $"净省 ${Format(netSaving, "0.0000")}，时间变化 {FormatSeconds(delta)}"
                : "净省不足或新节点过慢");
    }

    private static AdaptiveSwitchDecision DecideBalanced(
        AdaptiveSwitchRequest request,
        DurationConfiguration config,
        double remainingTokens,
        double penalty,
        double netSaving,
        double oldCompletion,
        double newCompletion,
        double delta)
    {
        var savingIsEnough = netSaving > 0.5 * penalty;
        var relativeTimeIsAcceptable = HasFiniteCompletionTimes(
                oldCompletion,
                newCompletion,
                delta) &&
            delta < 0.1 * oldCompletion;
        var timeIsAcceptable = newCompletion < config.ExpectedCompletionSeconds ||
            relativeTimeIsAcceptable;
        var priceReductionIsEnough = request.OldMultiplier > 0 &&
            (request.OldMultiplier - request.NewMultiplier) / request.OldMultiplier > 0.05;
        var accepted = savingIsEnough && timeIsAcceptable && priceReductionIsEnough;
        return Decision(
            accepted,
            accepted ? AdaptiveDecisionReason.AcceptedBalanced : AdaptiveDecisionReason.BalancedGuardRejected,
            AdaptivePreference.Balanced,
            remainingTokens,
            penalty,
            netSaving,
            oldCompletion,
            newCompletion,
            delta,
            accepted ? "净省及时间均满足条件" : "净省不足或时间超标或降价不够");
    }

    private static AdaptiveSwitchDecision DecideSpeed(
        AdaptiveSwitchRequest request,
        double remainingTokens,
        double penalty,
        double netSaving,
        double oldCompletion,
        double newCompletion,
        double delta)
    {
        var insufficientEvidence = request.NewPerformanceSampleCount is { } sampleCount &&
            sampleCount > 0 && sampleCount < AdaptiveRoutingConstants.MinimumSpeedPerformanceSamples;
        var speedBoost = double.IsFinite(request.OldGenerationSpeed) &&
            double.IsFinite(request.NewGenerationSpeed) &&
            request.OldGenerationSpeed > 0 &&
            request.NewGenerationSpeed > request.OldGenerationSpeed * 1.2;
        var priceOkForSpeed = request.NewMultiplier <= request.OldMultiplier * 1.1;
        var endToEndFaster = HasFiniteCompletionTimes(oldCompletion, newCompletion, delta) &&
            delta < -30;
        var priceNotHigher = request.NewMultiplier <= request.OldMultiplier;
        var accepted = !insufficientEvidence &&
            (speedBoost && priceOkForSpeed || endToEndFaster && priceNotHigher);
        return Decision(
            accepted,
            accepted
                ? AdaptiveDecisionReason.AcceptedSpeed
                : insufficientEvidence
                    ? AdaptiveDecisionReason.InsufficientPerformanceEvidence
                    : AdaptiveDecisionReason.SpeedGuardRejected,
            AdaptivePreference.Speed,
            remainingTokens,
            penalty,
            netSaving,
            oldCompletion,
            newCompletion,
            delta,
            accepted ? "速度提升显著，接受切换" : "速度提升不足或涨价过多");
    }

    private static AdaptiveSwitchDecision Rejected(
        AdaptiveDecisionReason reason,
        AdaptivePreference preference,
        double penalty,
        string detail,
        double remainingTokens = 0) =>
        Decision(
            false,
            reason,
            preference,
            remainingTokens,
            penalty,
            double.NegativeInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.NaN,
            detail);

    private static AdaptiveSwitchDecision Decision(
        bool shouldSwitch,
        AdaptiveDecisionReason reason,
        AdaptivePreference preference,
        double remainingTokens,
        double penalty,
        double netSaving,
        double oldCompletion,
        double newCompletion,
        double delta,
        string detail) =>
        new(
            shouldSwitch,
            reason,
            preference,
            remainingTokens,
            penalty,
            netSaving,
            oldCompletion,
            newCompletion,
            delta,
            detail);

    private static string FormatSeconds(double seconds) =>
        double.IsFinite(seconds) ? $"{Format(seconds, "0.0")} 秒" : "不可估算";

    private static bool HasFiniteCompletionTimes(double oldCompletion, double newCompletion, double delta) =>
        double.IsFinite(oldCompletion) &&
        double.IsFinite(newCompletion) &&
        double.IsFinite(delta);

    private static string Format(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
