using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestBalancedDeadlineUsesExplicitOutputBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.05, true, 1, now, 1_000, outputTps: 25),
                Provider(2, 0.01, true, 1, now, 1_000, outputTps: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(
                RoutingMode.Balanced,
                TaskDurationCategory.Medium,
                CurrentIntervalSeconds: 10,
                BalancedDeadlineSoftSeconds: 0,
                BalancedExpectedOutputTokens: 1_000),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.BalancedDeadlineDecision?.OutputTokens == 1_000,
            "Balanced deadline incorrectly derived output tokens from the task countdown.");
        Assert(BalancedDeadlineEngine.DefaultDeadlineSeconds == 26.73,
            "Balanced deadline changed the configured 90th-percentile SLA.");
    }

    internal static void TestBalancedConvenienceOverloadUsesDefaultOutputBudget()
    {
        var now = DateTimeOffset.Parse("2026-07-24T13:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [Provider(1, 0.01, true, 1, now, 1_000, outputTps: 100)],
            [Group(1)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Balanced),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.BalancedDeadlineDecision?.OutputTokens ==
            new PersistentAppSettings().BalancedExpectedOutputTokens,
            "The Balanced convenience overload did not use the persisted default output budget.");
    }

    internal static void TestCoordinatorUsesDefaultBalancedOutputBudgetWhenOmitted()
    {
        var now = DateTimeOffset.Parse("2026-07-24T13:00:00Z");
        var snapshot = RouteDecisionCoordinator.Evaluate(
            [Provider(1, 0.01, true, 1, now, 1_000, outputTps: 100)],
            [Group(1)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            TaskDurationCategory.Medium,
            new RouteState { CurrentGroupId = 1 },
            now,
            observedCurrentGroupId: 1);

        Assert(snapshot.Result.Decision.BalancedDeadlineDecision?.OutputTokens ==
            new PersistentAppSettings().BalancedExpectedOutputTokens,
            "The Coordinator did not use the persisted default Balanced output budget when it was omitted.");
    }

    internal static void TestLegacyAdaptiveContextPositionRemainsCompatible()
    {
        var now = DateTimeOffset.Parse("2026-07-24T13:30:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.05, true, 1, now, 1_000, outputTps: 25),
                Provider(2, 0.01, true, 1, now, 1_000, outputTps: 34.5)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(
                RoutingMode.Balanced,
                TaskDurationCategory.Medium,
                10,
                0,
                5,
                1_000),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.EffectivePreference == AdaptivePreference.Balanced &&
            result.Decision.BalancedDeadlineDecision is
            {
                OutputTokens: 1_000,
                Reason: BalancedDeadlineDecisionReason.SwitchedAfterDeadline
            },
            "The legacy context countdown slot changed Balanced mode or shifted the deadline inputs.");
    }

    internal static void TestLegacyCoordinatorPositionRemainsCompatible()
    {
        var now = DateTimeOffset.Parse("2026-07-24T13:30:00Z");
        var snapshot = RouteDecisionCoordinator.Evaluate(
            [
                Provider(1, 0.05, true, 1, now, 1_000, outputTps: 25,
                    lastCallEndedAt: now.AddSeconds(-10)),
                Provider(2, 0.01, true, 1, now, 1_000, outputTps: 34.5)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            TaskDurationCategory.Medium,
            new RouteState { CurrentGroupId = 1 },
            now,
            1,
            0,
            5,
            1_000);

        Assert(snapshot.Result.Decision.EffectivePreference == AdaptivePreference.Balanced &&
            snapshot.Result.Decision.BalancedDeadlineDecision is
            {
                OutputTokens: 1_000,
                Reason: BalancedDeadlineDecisionReason.SwitchedAfterDeadline
            },
            "The legacy Coordinator countdown slot changed Balanced mode or shifted the deadline inputs.");
    }

    internal static void TestBalancedDeadlineKeepsCurrentNode()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.05, true, 1, now, 1_000, outputTps: 100),
            Group(1),
            0.05,
            false);
        var cheaper = new RouteCandidate(
            Provider(2, 0.01, true, 1, now, 1_000, outputTps: 100),
            Group(2),
            0.01,
            false);

        var decision = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, cheaper],
            ExpectedOutputTokens: 1_000,
            CurrentIntervalSeconds: 10));

        Assert(!decision.ShouldSwitch && decision.Target?.Group.Id == 1 &&
            decision.Reason == BalancedDeadlineDecisionReason.CurrentWithinDeadline,
            "Balanced deadline switched away from a current node that met the SLA.");
    }

    internal static void TestBalancedDeadlineChoosesCheapestFeasibleNode()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.05, true, 1, now, 1_000, outputTps: 50),
            Group(1),
            0.05,
            false);
        var fasterButExpensive = new RouteCandidate(
            Provider(2, 0.03, true, 1, now, 500, outputTps: 200),
            Group(2),
            0.03,
            false);
        var cheapestFeasible = new RouteCandidate(
            Provider(3, 0.02, true, 1, now, 500, outputTps: 120),
            Group(3),
            0.02,
            false);

        var decision = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, fasterButExpensive, cheapestFeasible],
            ExpectedOutputTokens: 3_000,
            CurrentIntervalSeconds: 10));

        Assert(decision.ShouldSwitch && decision.Target?.Group.Id == 3 &&
            decision.Reason == BalancedDeadlineDecisionReason.SwitchedAfterDeadline,
            "Balanced deadline did not choose the lowest-cost feasible node after timeout.");
    }

    internal static void TestBalancedDeadlineColdStart()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.05, true, 1, now, 1_000, outputTps: 50),
            Group(1),
            0.05,
            false);
        var infeasibleCheap = new RouteCandidate(
            Provider(2, 0.01, true, 1, now, 500, outputTps: 100),
            Group(2),
            0.01,
            false);
        var feasible = new RouteCandidate(
            Provider(3, 0.02, true, 1, now, 500, outputTps: 120),
            Group(3),
            0.02,
            false);

        var decision = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, infeasibleCheap, feasible],
            ExpectedOutputTokens: 3_000,
            CurrentIntervalSeconds: 30.001,
            DeadlineSoftSeconds: 0));

        Assert(decision.ShouldSwitch && decision.Target?.Group.Id == 3 &&
            decision.Reason == BalancedDeadlineDecisionReason.ColdStart,
            "Balanced cold start did not choose the cheapest feasible node.");
    }

    internal static void TestBalancedDeadlineColdStartFallsBackToFastestRoute()
    {
        var now = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var current = new RouteCandidate(
            Provider(1, 0.01, true, 1, now, latency: 3_000, outputTps: 5),
            Group(1),
            0.01,
            false);
        var fastest = new RouteCandidate(
            Provider(2, 0.06, true, 1, now, latency: 1_000, outputTps: 20),
            Group(2),
            0.06,
            false);
        var middle = new RouteCandidate(
            Provider(3, 0.03, true, 1, now, latency: 2_000, outputTps: 8),
            Group(3),
            0.03,
            false);

        var decision = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, fastest, middle],
            ExpectedOutputTokens: 1_000,
            CurrentIntervalSeconds: 30.001,
            DeadlineSoftSeconds: 5));

        Assert(decision.IsColdStart && decision.ShouldSwitch && decision.Target?.Group.Id == 2 &&
            decision.Reason == BalancedDeadlineDecisionReason.FastestFallback,
            "A cold start with no feasible route did not fall back to the fastest route.");
    }

    internal static void TestBalancedDeadlineHonorsSoftTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new RouteCandidate(
            Provider(1, 0.05, true, 1, now, 1_000, outputTps: 25),
            Group(1),
            0.05,
            false);
        var affordable = new RouteCandidate(
            Provider(2, 0.01, true, 1, now, 1_000, outputTps: 34.5),
            Group(2),
            0.01,
            false);

        var withTolerance = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, affordable],
            ExpectedOutputTokens: 1_000,
            CurrentIntervalSeconds: 10,
            DeadlineSoftSeconds: 5));
        var withoutTolerance = BalancedDeadlineEngine.Decide(new BalancedDeadlineRequest(
            current,
            [current, affordable],
            ExpectedOutputTokens: 1_000,
            CurrentIntervalSeconds: 10,
            DeadlineSoftSeconds: 0));

        Assert(withTolerance.ShouldSwitch && withTolerance.Target?.Group.Id == 2 &&
            withTolerance.Reason == BalancedDeadlineDecisionReason.SwitchedAfterDeadline,
            "A feasible candidate inside the user soft deadline was not selected.");
        Assert(withoutTolerance.ShouldSwitch && withoutTolerance.Target?.Group.Id == 2 &&
            withoutTolerance.Reason == BalancedDeadlineDecisionReason.FastestFallback,
            "A zero soft tolerance did not fall back to the fastest route after every candidate missed the hard deadline.");
    }

    internal static void TestBalancedWithoutCountdownUsesDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 1, now, 1_000, outputTps: 100),
                Provider(2, 0.05, true, 1, now, 1_000, outputTps: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 2 },
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(
                RoutingMode.Balanced,
                TaskDurationCategory.Medium,
                CurrentIntervalSeconds: 10,
                BalancedExpectedOutputTokens: 1_000),
            now,
            observedCurrentGroupId: 2);

        Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2 &&
            result.Decision.EffectivePreference == AdaptivePreference.Balanced &&
            result.Decision.Reason == RouteDecisionReason.BalancedDeadlineCurrentWithinDeadline &&
            result.Decision.BalancedDeadlineDecision is not null,
            "Balanced routing did not use the Deadline engine after countdown removal.");
    }

    internal static void TestBalancedDeadlineUsesConservativePerformance()
    {
        var candidate = new RouteCandidate(
            new ProviderStatus
            {
                Id = "provider-1",
                GroupId = 1,
                Platform = "openai",
                FirstTokenLatencyMs = 100,
                OutputTokensPerSecond = 100,
                FirstTokenLatencyP90Ms = 2_000,
                OutputTokensPerSecondP25 = 10,
                PerformanceSampleCount = 20
            },
            Group(1),
            0.01,
            false);

        var completion = BalancedDeadlineEngine.CalculateCompletionSeconds(candidate, 100);
        Assert(Math.Abs(completion - 12) < 1e-12,
            "Balanced deadline did not use conservative performance percentiles.");
    }

    internal static void TestBalancedDeadlineInvalidCurrentIsForcedRecovery()
    {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [Provider(2, 0.02, true, 1, now, 1_000, outputTps: 50)],
            [Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 1,
                LastPolicySwitchAt = now,
                CompletedPolicyEvaluationsSinceLastSwitch = 0
            },
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(
                RoutingMode.Balanced,
                TaskDurationCategory.Medium,
                CurrentIntervalSeconds: 1,
                BalancedExpectedOutputTokens: 100),
            now,
            observedCurrentGroupId: 1);

        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2 &&
            result.Decision.Reason == RouteDecisionReason.CurrentRouteInvalid &&
            result.Decision.SwitchClass == RouteSwitchClass.ForcedRecovery,
            "Balanced routing misclassified an unavailable current group as a cold start.");
    }

    internal static void TestBalancedDeadlineSwitchUsesPolicyHysteresis()
    {
        var now = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 1_000, outputTps: 100),
                Provider(2, 0.05, true, 0.99, now, 1_000, outputTps: 10)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        var context = new AdaptiveRoutingContext(
            RoutingMode.Balanced,
            TaskDurationCategory.Medium,
            CurrentIntervalSeconds: 10,
            BalancedDeadlineSoftSeconds: 0,
            BalancedExpectedOutputTokens: 1_000);

        var coolingDown = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 2,
                LastPolicySwitchAt = now.AddSeconds(-29),
                CompletedPolicyEvaluationsSinceLastSwitch = 6,
                PendingPolicyTargetGroupId = 1,
                PendingPolicyTargetObservations = 2
            },
            Policy(RoutingMode.Balanced),
            context,
            now,
            observedCurrentGroupId: 2);
        Assert(!coolingDown.Decision.ShouldSwitch &&
            coolingDown.Decision.Reason == RouteDecisionReason.PolicySwitchCoolingDown,
            "Balanced Deadline bypassed the dwell guard.");

        var awaitingEvaluations = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 2,
                LastPolicySwitchAt = now.AddMinutes(-1),
                CompletedPolicyEvaluationsSinceLastSwitch = 5,
                PendingPolicyTargetGroupId = 1,
                PendingPolicyTargetObservations = 2
            },
            Policy(RoutingMode.Balanced),
            context,
            now,
            observedCurrentGroupId: 2);
        Assert(!awaitingEvaluations.Decision.ShouldSwitch &&
            awaitingEvaluations.Decision.Reason == RouteDecisionReason.PolicySwitchAwaitingEvaluations,
            "Balanced Deadline bypassed the completed-evaluation guard.");

        var awaitingStability = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 2,
                LastPolicySwitchAt = now.AddMinutes(-1),
                CompletedPolicyEvaluationsSinceLastSwitch = 6,
                PendingPolicyTargetGroupId = 1,
                PendingPolicyTargetObservations = 1
            },
            Policy(RoutingMode.Balanced),
            context,
            now,
            observedCurrentGroupId: 2);
        Assert(!awaitingStability.Decision.ShouldSwitch &&
            awaitingStability.Decision.Reason == RouteDecisionReason.PolicyCandidateNotStable,
            "Balanced Deadline bypassed stable-target observations.");

        var accepted = RouteDecisionEngine.Decide(
            evaluation,
            awaitingStability.NextState,
            Policy(RoutingMode.Balanced),
            context,
            now.AddSeconds(1),
            observedCurrentGroupId: 2);
        Assert(accepted.Decision.ShouldSwitch && accepted.Decision.Target?.Group.Id == 1 &&
            accepted.Decision.Reason == RouteDecisionReason.BalancedDeadlineSwitched,
            "A stable Balanced Deadline target was not eventually accepted.");
    }

    internal static void TestBalancedModeBuysLatency()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now, 10_000), Provider(2, 0.022, true, 0.99, now, 1_000)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        Assert(result.Recommended?.Group.Id == 2, "Balanced mode did not buy a large latency improvement.");
        Assert(result.CandidateScores.ContainsKey(2), "Balanced score was not exposed.");
    }

    internal static void TestBalancedModeKeepsPriceForModerateSpeedGap()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.03, true, 0.99, now, 6_051), Provider(2, 0.05, true, 0.99, now, 1_891)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        Assert(result.Recommended?.Group.Id == 1, "Balanced mode paid too much for a moderate latency gap.");
    }

    internal static void TestEconomyModeProtectsPrice()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now, 2_000), Provider(2, 0.022, true, 0.99, now, 1_000)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        Assert(result.Recommended?.Group.Id == 1, "Economy mode paid too much for a latency improvement.");
    }

    internal static void TestSpeedModeAcceptsLargerPremium()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now, 10_000), Provider(2, 0.04, true, 0.99, now, 2_000)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        Assert(result.Recommended?.Group.Id == 2, "Speed mode rejected a large latency gain.");
    }

}
