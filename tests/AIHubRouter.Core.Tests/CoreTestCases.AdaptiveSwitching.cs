using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestAdaptiveConstants()
    {
        Assert(AdaptiveRoutingConstants.InputPricePerMillion == 5.0, "Input price constant changed.");
        Assert(AdaptiveRoutingConstants.OutputPricePerMillion == 30.0, "Output price constant changed.");
        Assert(AdaptiveRoutingConstants.PenaltyTokens == 300_000, "Penalty token constant changed.");
        Assert(AdaptiveRoutingConstants.PlanningTokensPerSecond == 43.6, "Planning token rate was not doubled for sub-agent calls.");

        var shortConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Short);
        var mediumConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Medium);
        var longConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Long);
        Assert(shortConfig == new DurationConfiguration(0, 156_960, 3_600), "Short duration config changed.");
        Assert(mediumConfig == new DurationConfiguration(156_960, 627_840, 7_200), "Medium duration config changed.");
        Assert(longConfig == new DurationConfiguration(627_840, 3_767_040, 21_600), "Long duration config changed.");
    }

    internal static void TestAdaptivePreferenceBoundaries()
    {
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(4.999, AdaptivePreference.Cost) == AdaptivePreference.Cost,
            "An interval below five seconds overrode the user's Economy preference.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(5, AdaptivePreference.Cost) == AdaptivePreference.Cost,
            "The inclusive five-second boundary did not retain the base preference.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(15, AdaptivePreference.Balanced) == AdaptivePreference.Balanced,
            "The inclusive fifteen-second boundary did not retain the base preference.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(15.001, AdaptivePreference.Speed) == AdaptivePreference.Balanced,
            "Moderate idle time did not soften Speed to Balanced.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(15.001, AdaptivePreference.Balanced) == AdaptivePreference.Cost,
            "Moderate idle time did not shift Balanced to Cost.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(30, AdaptivePreference.Speed) == AdaptivePreference.Balanced,
            "The inclusive thirty-second boundary did not remain Balanced.");
        Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(30.001, AdaptivePreference.Speed) == AdaptivePreference.Cost,
            "An interval above thirty seconds did not force Cost.");
    }

    internal static void TestCurrentGroupIntervalResolution()
    {
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var providers = JsonSerializer.Deserialize<ProviderStatus[]>("""
            [
              {"group_id":1,"platform":"openai","lastCallEndedAt":"2026-07-21T09:59:48Z"},
              {"group_id":1,"platform":"openai","lastCallAt":"2026-07-21T09:59:53Z"},
              {"group_id":2,"platform":"openai","lastCallEndedAt":"2026-07-21T09:59:59Z"}
            ]
            """)!;

        var interval = AdaptiveSwitchDecisionEngine.ResolveCurrentIntervalSeconds(
            providers,
            currentGroupId: 1,
            platform: "openai",
            now: now);
        Assert(interval == 7, "A newer call from an unrelated group replaced the current-group interval.");
    }

    internal static void TestMissingCallTimeRetainsBasePreference()
    {
        var preference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
            currentIntervalSeconds: null,
            basePreference: AdaptivePreference.Balanced);
        Assert(preference == AdaptivePreference.Balanced, "A missing call timestamp fabricated an interval override.");
    }

    internal static void TestAdaptivePenalty()
    {
        var penalty = AdaptiveSwitchDecisionEngine.CalculatePenalty(0.02);
        Assert(Math.Abs(penalty - 0.03) < 1e-12, "The context-miss penalty did not use the new multiplier.");
    }

    internal static void TestAdaptiveCompletionTime()
    {
        var completion = AdaptiveSwitchDecisionEngine.CalculateCompletionTime(2, 20, 100);
        Assert(Math.Abs(completion - 7) < 1e-12, "TTFT was not included in completion time.");
        Assert(double.IsPositiveInfinity(AdaptiveSwitchDecisionEngine.CalculateCompletionTime(2, 0, 100)),
            "A non-positive generation speed did not produce infinite completion time.");
    }

    internal static void TestAdaptiveNetSaving()
    {
        var saving = AdaptiveSwitchDecisionEngine.CalculateNetSaving(0.02, 0.01, 313_920);
        Assert(Math.Abs(saving - 0.079176) < 1e-12, "Net saving did not subtract the context penalty.");
        Assert(double.IsNegativeInfinity(AdaptiveSwitchDecisionEngine.CalculateNetSaving(0.01, 0.01, 313_920)),
            "A non-lower price did not produce negative infinite saving.");
    }

    internal static void TestAdaptiveCostAcceptsPositiveSaving()
    {
        var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.01, 1, 1, 20, 20,
            TaskDurationCategory.Short, AdaptivePreference.Cost, 31));

        Assert(result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.AcceptedCost,
            "Cost mode rejected a positive net saving within the completion cap.");
        Assert(result.RemainingTokens == 156_960 && result.NetSavingUsd > 0,
            "Cost mode did not use the optimistic Short token estimate.");
    }

    internal static void TestAdaptiveCostRejectsSlowCandidate()
    {
        var exactBoundarySpeed = 156_960d / AdaptiveRoutingConstants.MaximumCostCompletionSeconds;
        var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.01, 0, 0, 20, exactBoundarySpeed,
            TaskDurationCategory.Short, AdaptivePreference.Cost, 31));

        Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.CostGuardRejected,
            "Cost mode accepted a candidate at the strict 24-hour boundary.");
    }

    internal static void TestAdaptiveBalancedSafeguards()
    {
        var accepted = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.05, 0.02, 1, 1, 20, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));
        var slow = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.05, 0.02, 1, 1, 20, 5,
            TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));
        var exactFivePercent = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.019, 1, 1, 20, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));

        Assert(accepted.ShouldSwitch && accepted.Reason == AdaptiveDecisionReason.AcceptedBalanced,
            "Balanced mode rejected a candidate satisfying every safeguard.");
        Assert(!slow.ShouldSwitch && slow.Reason == AdaptiveDecisionReason.BalancedGuardRejected,
            "Balanced mode accepted an excessive completion delay.");
        Assert(!exactFivePercent.ShouldSwitch,
            "Balanced mode accepted the strict five-percent price boundary.");
    }

    internal static void TestAdaptiveSpeedAcceptsGenerationBoost()
    {
        var accepted = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            10, 11, 1, 1, 20, 24.01,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2));
        var exactBoundary = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            10, 11, 1, 1, 20, 24,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2));

        Assert(accepted.ShouldSwitch && accepted.Reason == AdaptiveDecisionReason.AcceptedSpeed,
            "Speed mode rejected a generation boost above twenty percent at the price cap.");
        Assert(!exactBoundary.ShouldSwitch,
            "Speed mode accepted the strict 120-percent generation boundary.");
    }

    internal static void TestAdaptiveSpeedAcceptsEndToEndGain()
    {
        var accepted = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.02, 32, 1, 20, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2));
        var exactBoundary = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.02, 31, 1, 20, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2));

        Assert(accepted.ShouldSwitch, "Speed mode rejected an end-to-end gain above thirty seconds.");
        Assert(!exactBoundary.ShouldSwitch, "Speed mode accepted the strict thirty-second boundary.");
    }

    internal static void TestAdaptiveSpeedNeedsReliablePerformanceSamples()
    {
        var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.02, 1, 1, 20, 30,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2,
            OldPerformanceSampleCount: 20,
            NewPerformanceSampleCount: 19));

        Assert(!result.ShouldSwitch &&
            result.Reason == AdaptiveDecisionReason.InsufficientPerformanceEvidence,
            "Speed mode upgraded to a candidate with insufficient performance evidence.");
    }

    internal static void TestAdaptiveShortTaskProtection()
    {
        var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.05, 0.01, 1, 1, 20, 40,
            TaskDurationCategory.Short, AdaptivePreference.Balanced, 10));

        Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.ShortTaskProtected,
            "A short task switched outside Cost mode.");
    }

    internal static void TestAdaptiveInvalidPerformance()
    {
        var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.05, 0.02, 1, double.NaN, 20, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));

        Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.BalancedGuardRejected,
            "Invalid performance data accidentally satisfied the Balanced safeguards.");
    }

    internal static void TestAdaptiveInvalidOldPerformance()
    {
        var balanced = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.05, 0.02, double.NaN, 1, 20, 5,
            TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));
        var speed = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
            0.02, 0.02, 1, 1, 0, 20,
            TaskDurationCategory.Medium, AdaptivePreference.Speed, 2));

        Assert(!balanced.ShouldSwitch && balanced.Reason == AdaptiveDecisionReason.BalancedGuardRejected,
            "Invalid old performance satisfied the Balanced relative-time guard.");
        Assert(!speed.ShouldSwitch && speed.Reason == AdaptiveDecisionReason.SpeedGuardRejected,
            "Invalid old performance satisfied the Speed end-to-end guard.");
    }

    internal static void TestAdaptiveRejectionKeepsCurrentGroup()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.05, true, 0.99, now, 1_000, outputTps: 20),
                Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 5)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Medium, 10),
            now,
            observedCurrentGroupId: 1);
        Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
            "A rejected adaptive candidate replaced the current group.");
        Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveSpeedRejected,
            "The rejection did not expose its adaptive reason.");
    }

    internal static void TestAdaptiveAcceptanceUpdatesKeys()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now)
        {
            ProvidersOverride =
            [
                Provider(1, 0.05, true, 1, now, 1_000, outputTps: 20, lastCallEndedAt: now.AddSeconds(-10)),
                Provider(2, 0.02, true, 1, now, 1_000, outputTps: 20)
            ],
            GroupsOverride = [Group(1), Group(2)]
        };
        var settings = new PersistentAppSettings
        {
            RoutingMode = RoutingMode.Balanced,
            DurationCategory = TaskDurationCategory.Medium,
            KeySelectionInitialized = true,
            SelectedKeyIds = [10]
        };
        using var service = new RoutingService(
            settings,
            new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(api),
            utcNow: () => now);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();
        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2 && api.UpdateCalls == 1,
            "An accepted adaptive decision did not update the selected Key.");
        Assert(result.Decision.EffectivePreference == AdaptivePreference.Balanced &&
            result.Decision.CurrentIntervalSeconds == 10,
            "The service did not expose the interval-derived effective preference.");
    }

    internal static void TestAdaptiveTraversalFindsAcceptedCandidateBeyondWeightedWinner()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.10, true, 0.99, now, 1_000, outputTps: 100),
            Group(1),
            0.10,
            false);
        var weightedWinner = new RouteCandidate(
            Provider(2, 0.09, true, 0.99, now, 1_000, outputTps: 10),
            Group(2),
            0.09,
            false);
        var acceptedCandidate = new RouteCandidate(
            Provider(3, 0.01, true, 0.99, now, 1_000, outputTps: 150),
            Group(3),
            0.01,
            false);
        var evaluation = new RouteEvaluation(
            weightedWinner,
            acceptedCandidate,
            [current, weightedWinner, acceptedCandidate],
            new Dictionary<long, double> { [1] = 0, [2] = 1, [3] = 0.2 },
            0.01,
            0.80,
            0.20);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Medium, 10),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 3,
            "Adaptive traversal did not select the accepted candidate beyond the weighted winner.");
        Assert(Math.Abs(result.Decision.PricePremiumPercent) < 1e-12,
            "Adaptive traversal reported the weighted winner's price premium instead of the selected candidate's premium.");
        Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveSpeedAccepted,
            "Adaptive traversal did not preserve the accepted decision reason.");
    }

    internal static void TestAdaptiveRankingsFollowAcceptedAlgorithmOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.10, true, 0.99, now, 1_000, outputTps: 100),
            Group(1),
            0.10,
            false);
        var acceptedByNetSaving = new RouteCandidate(
            Provider(2, 0.05, true, 0.99, now, 1_000, outputTps: 130),
            Group(2),
            0.05,
            false);
        var bestNetSaving = new RouteCandidate(
            Provider(3, 0.02, true, 0.99, now, 1_000, outputTps: 130),
            Group(3),
            0.02,
            false);
        var rejectedByTime = new RouteCandidate(
            Provider(4, 0.01, true, 0.99, now, 1_000, outputTps: 1),
            Group(4),
            0.01,
            false);
        var evaluation = new RouteEvaluation(
            acceptedByNetSaving,
            acceptedByNetSaving,
            [current, acceptedByNetSaving, bestNetSaving, rejectedByTime],
            new Dictionary<long, double> { [1] = 0, [2] = 1, [3] = 0.5, [4] = 0.2 },
            0.02,
            0.80,
            0.20);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Medium, 10),
            now,
            observedCurrentGroupId: 1);

        var rankings = result.Decision.AdaptiveRankings;
        Assert(rankings.Select(ranking => ranking.GroupId).Take(2).SequenceEqual([3L, 2L]),
            "Adaptive rankings did not order accepted candidates by net saving.");
        Assert(rankings.Single(ranking => ranking.GroupId == 3).Rank == 1 &&
            rankings.Single(ranking => ranking.GroupId == 2).Rank == 2,
            "Accepted adaptive candidates did not receive sequential ranks.");
        Assert(rankings.Single(ranking => ranking.GroupId == 3).ProviderId == "provider-3" &&
            rankings.Single(ranking => ranking.GroupId == 2).ProviderId == "provider-2",
            "Adaptive rankings did not retain the provider that was actually evaluated.");
        Assert(!rankings.Single(ranking => ranking.GroupId == 4).Accepted &&
            rankings.Single(ranking => ranking.GroupId == 4).Rank is null &&
            rankings.Single(ranking => ranking.GroupId == 4).Reason == AdaptiveDecisionReason.SpeedGuardRejected,
            "Rejected adaptive candidates incorrectly occupied a suggestion rank.");
    }

    internal static void TestAdaptiveRecoveryBypassesGuard()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 0)],
            [Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        var initial = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState(),
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Short, 10),
            now);
        var invalid = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 9 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Short, 10),
            now,
            observedCurrentGroupId: 9);

        Assert(initial.Decision.ShouldSwitch && initial.Decision.Reason == RouteDecisionReason.InitialRoute,
            "Initial routing was blocked by pairwise safeguards.");
        Assert(invalid.Decision.ShouldSwitch && invalid.Decision.Reason == RouteDecisionReason.CurrentRouteInvalid,
            "Invalid-route recovery was blocked by pairwise safeguards.");
    }

    internal static void TestPolicySwitchNeedsStableCandidate()
    {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 1_000, outputTps: 50),
                Provider(2, 0.05, true, 0.99, now, 1_000, outputTps: 50)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var state = new RouteState
        {
            CurrentGroupId = 2,
            LastPolicySwitchAt = now.AddMinutes(-1),
            CompletedPolicyEvaluationsSinceLastSwitch = 6,
            PendingPolicyTargetGroupId = 1,
            PendingPolicyTargetObservations = 1
        };

        var first = RouteDecisionEngine.Decide(
            evaluation,
            state,
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 2);
        Assert(!first.Decision.ShouldSwitch &&
            first.Decision.Reason == RouteDecisionReason.PolicyCandidateNotStable,
            "A policy switch ignored the required second stable observation.");
        Assert(first.NextState.PendingPolicyTargetGroupId == 1 &&
            first.NextState.PendingPolicyTargetObservations == 2,
            "The first policy observation was not persisted for the next routing cycle.");

        var second = RouteDecisionEngine.Decide(
            evaluation,
            first.NextState,
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now.AddSeconds(1),
            observedCurrentGroupId: 2);
        Assert(second.Decision.ShouldSwitch && second.Decision.Target?.Group.Id == 1 &&
            second.Decision.SwitchClass == RouteSwitchClass.Policy,
            "A stable policy candidate did not switch after the hysteresis gates were satisfied.");
    }

    internal static void TestPolicySwitchRequiresDwellAndCompletedEvaluations()
    {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 1_000, outputTps: 50),
                Provider(2, 0.05, true, 0.99, now, 1_000, outputTps: 50)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);

        var dwellBlocked = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 2,
                LastPolicySwitchAt = now.AddSeconds(-29),
                CompletedPolicyEvaluationsSinceLastSwitch = 6,
                PendingPolicyTargetGroupId = 1,
                PendingPolicyTargetObservations = 2
            },
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 2);
        Assert(!dwellBlocked.Decision.ShouldSwitch &&
            dwellBlocked.Decision.Reason == RouteDecisionReason.PolicySwitchCoolingDown,
            "A policy switch bypassed the 30-second dwell period.");

        var countBlocked = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 2,
                LastPolicySwitchAt = now.AddMinutes(-1),
                CompletedPolicyEvaluationsSinceLastSwitch = 5,
                PendingPolicyTargetGroupId = 1,
                PendingPolicyTargetObservations = 2
            },
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 2);
        Assert(!countBlocked.Decision.ShouldSwitch &&
            countBlocked.Decision.Reason == RouteDecisionReason.PolicySwitchAwaitingEvaluations,
            "A policy switch bypassed the completed-evaluation gate.");
    }

    internal static void TestForcedRecoveryBypassesPolicyHysteresis()
    {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 50)],
            [Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 1,
                LastPolicySwitchAt = now,
                CompletedPolicyEvaluationsSinceLastSwitch = 0,
                PendingPolicyTargetGroupId = 2,
                PendingPolicyTargetObservations = 1
            },
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 1),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2 &&
            result.Decision.Reason == RouteDecisionReason.CurrentRouteInvalid &&
            result.Decision.SwitchClass == RouteSwitchClass.ForcedRecovery,
            "An unavailable current route was delayed by policy hysteresis.");
    }

}
