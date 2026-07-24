using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestEvidenceWeightDecayBoundaries()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var maximumAge = TimeSpan.FromMinutes(30);

        Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(now.AddMinutes(-30), now, maximumAge) - 1) < 0.000001,
            "Evidence at the thirty-minute freshness boundary was downweighted.");
        Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(now.AddMinutes(-60), now, maximumAge) - 0.5) < 0.000001,
            "Sixty-minute evidence did not receive half weight.");
        Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(now.AddHours(-8), now, maximumAge) - 0.25) < 0.000001,
            "Eight-hour evidence did not use the quarter-weight floor.");
        Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(now.AddMinutes(5), now, maximumAge) - 1) < 0.000001,
            "A future evidence timestamp produced a weight other than one.");
        Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(null, now, maximumAge) - 0.25) < 0.000001,
            "Missing timestamps did not use the performance-evidence floor.");
    }

    internal static void TestRouteCandidateDefaultsEvidenceWeight()
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new RouteCandidate(
            Provider(1, 0.01, available: true, success: 1, checkedAt: now),
            Group(1),
            0.01,
            HasUserRateOverride: false);

        Assert(Math.Abs(candidate.EvidenceWeight - 1) < 0.000001,
            "Legacy route-candidate construction did not default evidence weight to one.");
    }

    internal static void TestRoutingPublicModelsPreserveFourFieldCompatibility()
    {
        var blocklist = ProviderBlocklist.Empty;
        var criteria = new RoutingCriteria(
            "openai",
            0.5,
            RoutingEngine.DefaultMaximumStatusAge,
            blocklist)
        {
            ActiveProbeMaximumAge = TimeSpan.FromSeconds(180)
        };
        var (platform, minimumSuccessRate6h, maximumStatusAge, deconstructedBlocklist) = criteria;
        Assert(platform == "openai" &&
            minimumSuccessRate6h == 0.5 &&
            maximumStatusAge == RoutingEngine.DefaultMaximumStatusAge &&
            ReferenceEquals(blocklist, deconstructedBlocklist),
            "RoutingCriteria no longer supports its original four-field construction and deconstruction.");

        var successRates = new Dictionary<string, double> { ["6h"] = 1 };
        var warnings = new List<ProviderWarningReason>();
        var first = new ProviderStatus { Id = "same", SuccessRates = successRates, WarningReasons = warnings };
        var second = new ProviderStatus { Id = "same", SuccessRates = successRates, WarningReasons = warnings };
        Assert(!first.Equals(second),
            "ProviderStatus changed from reference identity to record value equality.");
    }

    internal static void TestRoutingStatusAgeDefaultsToThirtyMinutes()
    {
        var expected = TimeSpan.FromMinutes(30);

        Assert(new BalancedRoutingPolicy().MaximumStatusAge == expected &&
            new PersistentAppSettings().CreatePolicy().MaximumStatusAge == expected &&
            Criteria().MaximumStatusAge == expected &&
            Policy(RoutingMode.Balanced).MaximumStatusAge == expected,
            "Routing status-age defaults were not consistently thirty minutes.");
    }

    internal static void TestRoutingAcceptsStaleOrPerformanceEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var result = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 1, now.AddDays(-365), latency: null, outputTps: null),
                Provider(2, 0.02, true, 1, checkedAt: null, latency: null, outputTps: 40),
                Provider(3, 0.03, true, 1, checkedAt: null, latency: null, outputTps: null),
                Provider(4, 0.04, true, 1, checkedAt: null, latency: 400, outputTps: null)
            ],
            [Group(1), Group(2), Group(3), Group(4)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        var eligibleIds = result.EligibleCandidates.Select(candidate => candidate.Group.Id).ToArray();
        Assert(eligibleIds.SequenceEqual(new long[] { 1, 2, 4 }),
            "Routing did not accept stale timestamp or valid performance evidence while excluding missing evidence.");
        Assert(result.EligibleCandidates.All(candidate => Math.Abs(candidate.EvidenceWeight - 0.25) < 0.000001),
            "Timestamp-free performance or very old timestamp evidence did not receive floor weight.");
    }

    internal static void TestRoutingUsesLatestEvidenceTimestamp()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var activeProbeTtl = TimeSpan.FromMinutes(60);
        var result = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.9, now, latency: 1_000),
                Provider(2, 0.011, true, 0.9, now.AddMinutes(-60), latency: 500,
                    activeProbeCheckedAt: now.AddMinutes(-45), activeProbeHealthy: true)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed) with { ActiveProbeMaximumAge = activeProbeTtl },
            now);

        var candidate = result.EligibleCandidates.Single(item => item.Group.Id == 2);
        Assert(Math.Abs(candidate.EvidenceWeight - (2d / 3d)) < 0.000001,
            "Routing did not use the latest of status and active-probe evidence timestamps.");

        var disabled = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.9, now, latency: 1_000),
                Provider(2, 0.011, true, 0.9, now.AddMinutes(-60), latency: 500,
                    activeProbeCheckedAt: now, activeProbeHealthy: true, activeProbeLatency: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        Assert(disabled.EligibleCandidates.Single(item => item.Group.Id == 2).EvidenceWeight == 0.5,
            "A disabled probe policy treated cached probe health as fresh evidence.");
        Assert(disabled.EligibleCandidates.Single(item => item.Group.Id == 2).Provider.FirstTokenLatencyMs == 500,
            "A disabled probe policy let cached local TTFT override provider data.");
        Assert(!RoutingEngine.UsesFreshActiveProbeLatency(
                disabled.EligibleCandidates.Single(item => item.Group.Id == 2).Provider,
                now,
                activeProbeMaximumAge: null),
            "A disabled probe policy still labeled cached local TTFT as active.");
    }

    internal static void TestFreshActiveProbeFailureIsExcludedAtTtlBoundary()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromSeconds(180);
        var failed = Provider(
            1,
            0.01,
            true,
            1,
            now.AddHours(-1),
            latency: 500,
            activeProbeCheckedAt: now - ttl,
            activeProbeHealthy: false);
        var healthy = Provider(2, 0.02, true, 1, now, latency: 600);
        var policy = Policy(RoutingMode.Economy) with { ActiveProbeMaximumAge = ttl };

        var evaluation = RoutingEngine.Evaluate(
            [failed, healthy],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            policy,
            now);

        Assert(evaluation.EligibleCandidates.Select(candidate => candidate.Group.Id).SequenceEqual([2L]),
            "A probe failure at the TTL boundary remained route eligible.");
        Assert(!ProviderStatusPresentation.IsRoutable(
                failed,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: failed.PriceMultiplier,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge,
                ttl),
            "Presentation accepted a fresh probe failure rejected by routing.");
        Assert(ProviderStatusPresentation.ResolveRoutingState(
                failed,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: failed.PriceMultiplier,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge,
                ttl) == "健康检查失败",
            "Presentation did not identify the fresh probe failure safely.");
        Assert(!RoutingEngine.UsesFreshActiveProbeLatency(failed, now, ttl),
            "A fresh failed probe was labeled as usable local latency.");
    }

    internal static void TestExpiredActiveProbeFailureIsNeutral()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromSeconds(180);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(
            now - ttl - TimeSpan.FromTicks(2),
            [Provider(1, 0.01, true, 1, now.AddMinutes(-60), latency: 2_000)],
            new Dictionary<long, double>());
        var expiredFailure = window.RecordActiveProbeObservations(
        [
            new ActiveProbeObservation("openai", 1, now - ttl - TimeSpan.FromTicks(2), true, 100),
            new ActiveProbeObservation("openai", 1, now - ttl - TimeSpan.FromTicks(1), false)
        ]).Providers.Single();
        var policy = Policy(RoutingMode.Economy) with { ActiveProbeMaximumAge = ttl };

        var evaluation = RoutingEngine.Evaluate(
            [expiredFailure],
            [Group(1)],
            new Dictionary<long, double>(),
            policy,
            now);

        Assert(evaluation.EligibleCandidates.Single().Group.Id == 1,
            "An expired probe failure did not fall back to provider evidence.");
        Assert(evaluation.EligibleCandidates.Single().Provider.FirstTokenLatencyMs == 2_000,
            "An expired failure left an older successful local TTFT overriding provider data.");
        Assert(ProviderStatusPresentation.IsRoutable(
                expiredFailure,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: expiredFailure.PriceMultiplier,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge,
                ttl),
            "Presentation did not treat an expired probe failure as neutral.");
        Assert(ProviderStatusPresentation.ResolveRoutingState(
                expiredFailure,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: expiredFailure.PriceMultiplier,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge,
                ttl) == "数据陈旧（已降权）",
            "Expired failure freshness incorrectly refreshed stale provider evidence.");
        Assert(!RoutingEngine.UsesFreshActiveProbeLatency(expiredFailure, now, ttl),
            "An expired failed probe was labeled as usable local latency.");
    }

    internal static void TestFreshActiveProbeSuccessProvidesFreshnessEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromSeconds(180);
        var provider = Provider(
            1,
            0.01,
            true,
            1,
            checkedAt: null,
            latency: null,
            outputTps: null,
            activeProbeCheckedAt: now - ttl,
            activeProbeHealthy: true,
            activeProbeLatency: 100);
        var policy = Policy(RoutingMode.Economy) with { ActiveProbeMaximumAge = ttl };

        var evaluation = RoutingEngine.Evaluate(
            [provider],
            [Group(1)],
            new Dictionary<long, double>(),
            policy,
            now);

        Assert(evaluation.EligibleCandidates.Single().EvidenceWeight == 1,
            "A fresh successful probe was not usable freshness evidence.");
        Assert(RoutingEngine.UsesFreshActiveProbeLatency(provider, now, ttl),
            "A fresh successful local TTFT was not identified for presentation.");
        Assert(ProviderStatusPresentation.IsRoutable(
                provider,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: provider.PriceMultiplier,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge,
                ttl),
            "Presentation disagreed with fresh successful probe evidence.");

        var expired = Provider(
            1,
            0.01,
            true,
            1,
            checkedAt: null,
            latency: null,
            outputTps: null,
            activeProbeCheckedAt: now - ttl - TimeSpan.FromTicks(1),
            activeProbeHealthy: true,
            activeProbeLatency: 100);
        var expiredEvaluation = RoutingEngine.Evaluate(
            [expired],
            [Group(1)],
            new Dictionary<long, double>(),
            policy,
            now);
        Assert(expiredEvaluation.EligibleCandidates.Count == 0,
            "An expired success timestamp remained freshness evidence without provider data.");
        Assert(!RoutingEngine.UsesFreshActiveProbeLatency(expired, now, ttl),
            "An expired successful probe was still labeled as usable local latency.");
    }

    internal static void TestExpiredActiveProbeSuccessRestoresProviderLatency()
    {
        var observedAt = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromSeconds(180);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(
            observedAt,
            [Provider(1, 0.01, true, 1, observedAt, latency: 2_000, outputTps: 20)],
            new Dictionary<long, double>());
        var provider = window.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 1, observedAt, true, 100)])
            .Providers.Single();
        var policy = Policy(RoutingMode.Economy) with { ActiveProbeMaximumAge = ttl };

        var fresh = RoutingEngine.Evaluate(
            [provider],
            [Group(1)],
            new Dictionary<long, double>(),
            policy,
            observedAt + ttl);
        Assert(fresh.EligibleCandidates.Single().Provider.FirstTokenLatencyMs == 100,
            "A successful probe at the TTL boundary did not override provider TTFT.");

        var expired = RoutingEngine.Evaluate(
            [provider],
            [Group(1)],
            new Dictionary<long, double>(),
            policy,
            observedAt + ttl + TimeSpan.FromTicks(1));
        Assert(expired.EligibleCandidates.Single().Provider.FirstTokenLatencyMs == 2_000,
            "Expired local TTFT continued to override the provider TTFT.");
        Assert(!RoutingEngine.UsesFreshActiveProbeLatency(provider, observedAt + ttl + TimeSpan.FromTicks(1), ttl),
            "Expired local TTFT remained marked as the active presentation source.");
    }

    internal static void TestStalePositiveBenefitsAreDownweighted()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

        RouteEvaluation EvaluateAt(DateTimeOffset checkedAt) => RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.5, now, latency: 1_000),
                Provider(2, 0.011, true, 0.9, checkedAt, latency: 500)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        var fresh = EvaluateAt(now);
        var stale = EvaluateAt(now.AddMinutes(-60));

        Assert(Math.Abs(fresh.CandidateScores[2] - 0.675) < 0.000001,
            "Fresh positive speed and reliability benefits used the wrong score formula.");
        Assert(Math.Abs(stale.CandidateScores[2] - 0.32) < 0.000001,
            "Stale positive speed and reliability benefits were not downweighted.");
        Assert(Math.Abs(stale.EligibleCandidates.Single(candidate => candidate.Group.Id == 2).EvidenceWeight - 0.5) < 0.000001,
            "The stale candidate did not expose its half evidence weight.");
    }

    internal static void TestEvidenceDecayPreservesPenalties()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

        RouteEvaluation EvaluatePricePenalty(DateTimeOffset checkedAt) => RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.5, now, latency: 1_000),
                Provider(2, 0.011, true, 0.5, checkedAt, latency: 1_000)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        RouteEvaluation EvaluateNegativeBenefits(DateTimeOffset checkedAt) => RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.9, now, latency: 500),
                Provider(2, 0.01, true, 0.5, checkedAt, latency: 1_000)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        var freshPriceScore = EvaluatePricePenalty(now).CandidateScores[2];
        var stalePriceScore = EvaluatePricePenalty(now.AddMinutes(-60)).CandidateScores[2];
        Assert(Math.Abs(freshPriceScore + 0.035) < 0.000001 &&
            Math.Abs(stalePriceScore - freshPriceScore) < 0.000001,
            "Evidence decay reduced the price-premium penalty.");

        var freshNegativeScore = EvaluateNegativeBenefits(now).CandidateScores[2];
        var staleNegativeScore = EvaluateNegativeBenefits(now.AddMinutes(-60)).CandidateScores[2];
        Assert(Math.Abs(freshNegativeScore + 0.385) < 0.000001 &&
            Math.Abs(staleNegativeScore - freshNegativeScore) < 0.000001,
            "Evidence decay reduced negative speed or reliability deltas.");
    }

    internal static void TestReliabilityDeltaContributesToScore()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

        RouteEvaluation Evaluate(double candidateSuccess, DateTimeOffset checkedAt, bool includeSuccess = true) =>
            RoutingEngine.Evaluate(
                [
                    Provider(1, 0.01, true, 0.5, now, latency: 1_000),
                    Provider(2, 0.011, true, candidateSuccess, checkedAt, latency: 1_000,
                        includeSuccess: includeSuccess)
                ],
                [Group(1), Group(2)],
                new Dictionary<long, double>(),
                Policy(RoutingMode.Speed),
                now);

        var freshControl = Evaluate(0.5, now).CandidateScores[2];
        var freshReliable = Evaluate(0.9, now).CandidateScores[2];
        Assert(Math.Abs((freshReliable - freshControl) - 0.15 * 0.4) < 0.000001,
            "Fresh reliability delta did not contribute exactly 0.15 times the delta.");

        var staleControl = Evaluate(0.5, now.AddMinutes(-60)).CandidateScores[2];
        var staleReliable = Evaluate(0.9, now.AddMinutes(-60)).CandidateScores[2];
        Assert(Math.Abs((staleReliable - staleControl) - 0.15 * 0.4 * 0.5) < 0.000001,
            "A positive stale reliability delta was not weighted by evidence age.");

        var missingSuccess = Evaluate(0, now, includeSuccess: false).CandidateScores[2];
        var explicitZero = Evaluate(0, now).CandidateScores[2];
        Assert(Math.Abs(missingSuccess - explicitZero) < 0.000001,
            "Missing six-hour success evidence was not scored as zero.");
    }

    internal static void TestReliabilityWeightConstant()
    {
        Assert(Math.Abs(RoutingEngine.ReliabilityWeight - 0.15) < 0.000001,
            "Routing reliability weight changed from 0.15.");
    }

    internal static void TestReliabilityScoreNormalizesRawRates()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

        RouteEvaluation Evaluate(double baselineSuccess, double candidateSuccess) => RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, baselineSuccess, now, latency: 1_000),
                Provider(2, 0.011, true, candidateSuccess, now, latency: 1_000)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        Assert(Math.Abs(Evaluate(0, 2).CandidateScores[2] - 0.115) < 0.000001,
            "A finite out-of-range candidate success rate was not clamped to one.");
        Assert(Math.Abs(Evaluate(0, double.PositiveInfinity).CandidateScores[2] + 0.035) < 0.000001,
            "A non-finite candidate success rate contributed to ranking.");
        Assert(Math.Abs(Evaluate(2, 1).CandidateScores[2] + 0.035) < 0.000001,
            "An out-of-range baseline success rate was not normalized before comparison.");
    }

    internal static void TestReliabilityEligibilityAndTieBreaksNormalizeRawRates()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        var thresholdPolicy = Policy(RoutingMode.Economy) with { MinimumSuccessRate6h = 0.5 };
        var thresholdEvaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, double.NaN, now),
                Provider(2, 0.02, true, double.PositiveInfinity, now),
                Provider(3, 0.03, true, 2, now),
                Provider(4, 0.04, true, 0.75, now),
                Provider(5, 0.005, true, -1, now)
            ],
            [Group(1), Group(2), Group(3), Group(4), Group(5)],
            new Dictionary<long, double>(),
            thresholdPolicy,
            now);
        Assert(thresholdEvaluation.EligibleCandidates.Select(candidate => candidate.Group.Id).SequenceEqual([3L, 4L]),
            "Non-finite reliability bypassed the threshold or finite out-of-range reliability was not clamped.");

        var duplicateGroup = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, double.PositiveInfinity, now, id: "invalid"),
                Provider(1, 0.01, true, 0.9, now, id: "valid")
            ],
            [Group(1)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        Assert(duplicateGroup.EligibleCandidates.Single().Provider.Id == "valid",
            "A non-finite raw success rate won a duplicate-group tie-break.");

        var cheapest = RoutingEngine.SelectCheapest(
            [
                Provider(1, 0.01, true, double.NaN, now),
                Provider(2, 0.02, true, 0.75, now)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            new RoutingCriteria("openai", 0.5, RoutingEngine.DefaultMaximumStatusAge),
            now);
        Assert(cheapest?.Group.Id == 2,
            "SelectCheapest let non-finite reliability bypass the threshold.");

        var cheapestDuplicate = RoutingEngine.SelectCheapest(
            [
                Provider(1, 0.01, true, double.PositiveInfinity, now, id: "invalid"),
                Provider(1, 0.01, true, 0.9, now, id: "valid")
            ],
            [Group(1)],
            new Dictionary<long, double>(),
            Criteria(),
            now);
        Assert(cheapestDuplicate?.Provider.Id == "valid",
            "SelectCheapest let non-finite reliability win a tie-break.");

        var invalid = Provider(5, 0.01, true, double.NaN, now);
        Assert(!ProviderStatusPresentation.IsRoutable(
                invalid,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: invalid.PriceMultiplier,
                minimumSuccessRate6h: 0.5,
                now,
                RoutingEngine.DefaultMaximumStatusAge),
            "Presentation let non-finite reliability bypass the threshold.");
        Assert(ProviderStatusPresentation.ResolveRoutingState(
                invalid,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: invalid.PriceMultiplier,
                minimumSuccessRate6h: 0.5,
                now,
                RoutingEngine.DefaultMaximumStatusAge) == "低于阈值",
            "Presentation did not normalize reliability consistently with routing.");

        var aboveOne = Provider(6, 0.01, true, 2, now);
        Assert(ProviderStatusPresentation.IsRoutable(
                aboveOne,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: aboveOne.PriceMultiplier,
                minimumSuccessRate6h: 1,
                now,
                RoutingEngine.DefaultMaximumStatusAge),
            "Presentation did not clamp finite reliability above one.");
    }

    internal static void TestSelectivePolicyPreservesLocalWeights()
    {
        Assert(Policy(RoutingMode.Economy).PriceWeight == 0.95, "Economy weight changed.");
        Assert(Policy(RoutingMode.Balanced).PriceWeight == 0.80, "Balanced weight changed.");
        Assert(Policy(RoutingMode.Speed).PriceWeight == 0.35, "Speed weight changed.");
        Assert(new PersistentAppSettings().RoutingMode == RoutingMode.Economy, "Default mode changed.");
    }

    internal static void TestCostModeProposesCheapest()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 2_000, outputTps: 20),
                Provider(2, 0.011, true, 0.99, now, 100, outputTps: 100),
                Provider(3, 0.05, true, 0.99, now, 500, outputTps: 20)
            ],
            [Group(1), Group(2), Group(3)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 3 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 3);
        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
            "Cost mode did not select the strict lowest-price baseline.");
    }

    internal static void TestCostModeFallsBackWhenCheapestTtftIsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, null, outputTps: 20),
                Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 20),
                Provider(3, 0.05, true, 0.99, now, 500, outputTps: 20)
            ],
            [Group(1), Group(2), Group(3)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        Assert(evaluation.Baseline?.Group.Id == 2,
            "Test setup did not reproduce the measured-only baseline behavior.");

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 3 },
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 3);

        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
            "Economy mode did not select the strict lowest effective multiplier.");
        Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveCostAccepted,
            "Strict Economy selection did not preserve the accepted cost reason.");
    }

    internal static void TestFirstTokenKeepsStaleProviderEligible()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddMinutes(-16);
        var result = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, stale, latency: 1_500),
                Provider(2, 0.02, true, 0.99, checkedAt: null, latency: null, outputTps: null)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);

        Assert(result.EligibleCandidates.Count == 1 &&
            result.EligibleCandidates[0].Group.Id == 1 &&
            result.EligibleCandidates[0].Provider.FirstTokenLatencyMs == 1_500,
            "A provider with a valid first-token measurement was incorrectly rejected as stale.");
        Assert(ProviderStatusPresentation.IsRoutable(
                result.EligibleCandidates[0].Provider,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: 0.01,
                minimumSuccessRate6h: 0,
                now,
                RoutingEngine.DefaultMaximumStatusAge),
            "The grid presentation disagreed with routing eligibility for first-token evidence.");
    }

    internal static void TestStaleFirstTokenCandidatesParticipateInRanking()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddHours(-1);
        var result = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, stale, latency: 2_000),
                Provider(2, 0.011, true, 0.99, stale, latency: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        Assert(result.EligibleCandidates.Count == 2 &&
            result.CandidateScores.ContainsKey(1) &&
            result.CandidateScores.ContainsKey(2) &&
            result.Recommended?.Group.Id == 2,
            "Stale providers with valid first-token data did not participate in weighted ranking.");
    }

    internal static void TestForcedGroupOverridesRoutingPolicyUntilUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, latency: 2_000),
                Provider(2, 0.20, true, 0.99, now, latency: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);

        var forced = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState
            {
                CurrentGroupId = 1,
                ForcedGroupId = 2,
                LastPolicySwitchAt = now,
                CompletedPolicyEvaluationsSinceLastSwitch = 0
            },
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Medium, 10),
            now,
            observedCurrentGroupId: 1);

        Assert(forced.Decision.ShouldSwitch &&
            forced.Decision.Target?.Group.Id == 2 &&
            forced.Decision.Reason == RouteDecisionReason.ForcedGroupSelected &&
            forced.NextState.ForcedGroupId == 2,
            "A forced group did not override the normal routing policy.");

        var unavailableEvaluation = RoutingEngine.Evaluate(
            [Provider(1, 0.01, true, 0.99, now, latency: 2_000)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        var recovered = RouteDecisionEngine.Decide(
            unavailableEvaluation,
            forced.NextState,
            Policy(RoutingMode.Balanced),
            new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Medium, 10),
            now,
            observedCurrentGroupId: 2);

        Assert(recovered.NextState.ForcedGroupId is null &&
            recovered.Decision.Target?.Group.Id == 1,
            "An unavailable forced group was not cleared before normal recovery.");
    }

    internal static void TestReleasingForcedGroupResetsPolicyObservation()
    {
        var state = new RouteState
        {
            CurrentGroupId = 7,
            ForcedGroupId = 9,
            LastPolicySwitchAt = DateTimeOffset.UtcNow,
            CompletedPolicyEvaluationsSinceLastSwitch = 4,
            PendingPolicyTargetGroupId = 11,
            PendingPolicyTargetObservations = 2
        };

        var released = state.ReleaseForcedGroup();

        Assert(released.ForcedGroupId is null &&
            released.LastPolicySwitchAt is null &&
            released.CompletedPolicyEvaluationsSinceLastSwitch == 0 &&
            released.PendingPolicyTargetGroupId is null &&
            released.PendingPolicyTargetObservations == 0 &&
            released.CurrentGroupId == state.CurrentGroupId,
            "Releasing a forced group did not reset policy observations while preserving the current route.");
    }

    internal static void TestFrequentCallsOverrideEconomy()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 1_000, outputTps: 20),
                Provider(2, 0.0105, true, 0.99, now, 100, outputTps: 30)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);

        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Economy, TaskDurationCategory.Medium, 2),
            now,
            observedCurrentGroupId: 1);
        Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
            "Economy mode did not keep the strict lowest-cost route during a frequent call interval.");
        Assert(result.Decision.EffectivePreference == AdaptivePreference.Cost,
            "Economy mode was incorrectly overridden with Speed.");
    }

    internal static void TestIdleCallsOverrideSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now, 1_000, outputTps: 20),
                Provider(2, 0.05, true, 0.99, now, 100, outputTps: 100)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 2 },
            Policy(RoutingMode.Economy),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Short, 31),
            now,
            observedCurrentGroupId: 2);
        Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
            "An idle Speed preference did not switch to the cheaper candidate.");
        Assert(result.Decision.EffectivePreference == AdaptivePreference.Cost,
            "Idle time did not override Speed with Cost.");
    }

    internal static void TestMissingLatencyRanksLast()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now, null), Provider(2, 0.02, true, 0.99, now, 2_000)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        Assert(result.Recommended?.Group.Id == 2, "Missing latency outranked a measured latency.");
    }

    internal static void TestInvalidMeasurementsAreExcluded()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [
                Provider(1, double.NaN, true, 0.99, now, 100),
                Provider(2, double.PositiveInfinity, true, 0.99, now, 100),
                Provider(3, -0.01, true, 0.99, now, 100),
                Provider(4, 0.04, true, 0.99, now, 100)
            ],
            [Group(1), Group(2), Group(3), Group(4)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Balanced),
            now);
        Assert(result.EligibleCandidates.Count == 1 && result.Recommended?.Group.Id == 4,
            "Invalid multiplier measurements were not filtered.");
    }

    internal static void TestExtremeLatencyScoresStayFinite()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now, double.MaxValue), Provider(2, 0.021, true, 0.99, now, double.Epsilon)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        Assert(result.CandidateScores.Values.All(double.IsFinite), "Extreme latency produced a non-finite score.");
    }

    internal static void TestZeroMultiplierWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var result = RoutingEngine.Evaluate(
            [Provider(1, 0, true, 0.99, now, 8_000), Provider(2, 0.001, true, 0.99, now, 100)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        Assert(result.Recommended?.Group.Id == 1, "Zero multiplier route was not retained.");
    }

    internal static void TestInitialRouteDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now), Provider(2, 0.01, true, 0.99, now)],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var result = RouteDecisionEngine.Decide(evaluation, new RouteState(), Policy(RoutingMode.Economy), now);
        Assert(result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.InitialRoute,
            "Initial route was not explained as an initial route.");
        Assert(result.NextState.CurrentGroupId == 2, "Initial route state did not target the recommendation.");
    }

    internal static void TestWeightedSpeedWinnerSwitchesImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.02, true, 0.99, now, 10_000, outputTps: 20),
                Provider(2, 0.021, true, 0.99, now, 1_000, outputTps: 30)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Speed),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Speed),
            new AdaptiveRoutingContext(RoutingMode.Speed, TaskDurationCategory.Medium, 2),
            now,
            1);
        Assert(result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.AdaptiveSpeedAccepted,
            "A candidate above the adaptive Speed threshold did not switch.");
    }

    internal static void TestPreviewAndSimulationShareInitialDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var providers = new[]
        {
            Provider(1, 0.01, true, 0.99, now, 10_000),
            Provider(2, 0.02, true, 0.99, now, 100)
        };
        var snapshot = RouteDecisionCoordinator.Evaluate(
            providers,
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            TaskDurationCategory.Medium,
            new RouteState(),
            now);

        Assert(snapshot.Result.Decision.Target?.Group.Id == 1,
            "Preview and simulation did not share the strict-cheapest initial route.");
        Assert(snapshot.Evaluation.Recommended?.Group.Id == 2,
            "Regression setup no longer separates weighted and strict-cheapest recommendations.");
    }

    internal static void TestAlreadyOptimalRouteDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [Provider(1, 0.02, true, 0.99, now)],
            [Group(1)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Economy),
            now,
            1);
        Assert(!result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.AlreadyOptimal,
            "An already optimal route was not recognized.");
    }

    internal static void TestInvalidCurrentRouteDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [Provider(2, 0.02, true, 0.99, now)],
            [Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 1 },
            Policy(RoutingMode.Economy),
            now,
            1);
        Assert(result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.CurrentRouteInvalid,
            "An invalid current route was not explained.");
    }

    internal static void TestNoCandidateDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            Array.Empty<ProviderStatus>(),
            Array.Empty<GroupInfo>(),
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy),
            now);
        var result = RouteDecisionEngine.Decide(
            evaluation,
            new RouteState { CurrentGroupId = 7 },
            Policy(RoutingMode.Economy),
            now,
            7);
        Assert(!result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.NoCandidate,
            "No-candidate route did not preserve a safe decision.");
        Assert(result.NextState.CurrentGroupId == 7, "No-candidate route lost the observed group.");
    }

    internal static void TestRouteStateRoundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonRouteStateStore(directory);
            store.Save(new RouteState { CurrentGroupId = 42, ForcedGroupId = 24 });
            var loaded = store.Load();
            Assert(loaded.CurrentGroupId == 42 && loaded.ForcedGroupId == 24,
                "Route state did not roundtrip.");
            Assert(!File.Exists(Path.Combine(directory, "route-state.json.tmp")), "Temporary route state was left behind.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    internal static void TestUnreadableRouteStateResets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "route-state.json"), "{not-json");
            Assert(new JsonRouteStateStore(directory).Load().CurrentGroupId is null,
                "Unreadable route state was allowed to break startup.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    internal static void TestRoutingServiceRetainsSevenParameterConstructor()
    {
        var constructor = typeof(RoutingService).GetConstructor(
        [
            typeof(PersistentAppSettings),
            typeof(PersistentCredentials),
            typeof(IRouteStateStore),
            typeof(IAIHubClientFactory),
            typeof(Func<PersistentCredentials, CancellationToken, Task>),
            typeof(Func<DateTimeOffset>),
            typeof(ProviderMetricsRollingWindow)
        ]);

        Assert(constructor is not null,
            "RoutingService no longer exposes its seven-parameter public constructor.");
    }

    internal static void TestDryRunNeverUpdatesKey()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now);
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(
            settings,
            new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(api),
            utcNow: () => now);

        var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
        Assert(result.Decision.ShouldSwitch, "Dry run did not calculate a switch.");
        Assert(api.UpdateCalls == 0, "Dry run called UpdateKeyGroupAsync.");
        Assert(result.KeyResults.Single().Changed, "Dry run did not report the proposed Key change.");
    }

    internal static void TestAccountDataCache()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now);
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
        service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
        Assert(api.SummaryCalls == 2, "Monitor summary was not fetched every cycle.");
        Assert(api.GroupsCalls == 1 && api.RatesCalls == 1 && api.KeysCalls == 1,
            "Account data was not cached.");
    }

    internal static void TestRoutingResultExposesUserRates()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { UserRateOverride = 0.007 };
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
        Assert(result.UserGroupRates.TryGetValue(2, out var rate) && Math.Abs(rate - 0.007) < 0.000001,
            "Routing result did not expose the account rate used for evaluation.");
    }

    internal static void TestForcedAccountRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now);
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
        service.RunOnceAsync(dryRun: true, forceAccountRefresh: true).GetAwaiter().GetResult();
        Assert(api.GroupsCalls == 2 && api.RatesCalls == 2 && api.KeysCalls == 2,
            "Forced account refresh did not bypass the cache.");
    }

    internal static void TestRoutingNetworkFailureDoesNotLogin()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { ThrowNetwork = true };
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials
        {
            Email = "user@example.test", Password = "synthetic-password", RefreshToken = "synthetic-refresh",
            AccessTokenExpiresAt = now.AddHours(1), BearerToken = "synthetic-access"
        }, new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        try
        {
            service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
            throw new InvalidOperationException("Network failure was swallowed.");
        }
        catch (HttpRequestException)
        {
            Assert(api.RefreshCalls == 0 && api.LoginCalls == 0, "Network failure triggered authentication.");
        }
    }

    internal static void TestRoutingRejectsEmptySelection()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now);
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        try
        {
            service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
            throw new InvalidOperationException("Empty Key selection was accepted.");
        }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message.Contains("Key", StringComparison.OrdinalIgnoreCase),
                "Empty selection did not produce a safe Key guidance error.");
        }
    }

    internal static void TestSuccessfulRoutePersistsState()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now);
        var state = new MemoryRouteStateStore();
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            state, new StubRoutingClientFactory(api), utcNow: () => now);
        var result = service.RunOnceAsync().GetAwaiter().GetResult();
        Assert(result.ChangedKeyCount == 1 && result.FailedKeyCount == 0, "Successful route update was not reported.");
        Assert(state.Load().CurrentGroupId == 2, "Successful route did not persist target state.");
    }

    internal static void TestPartialFailureClearsState()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { TwoKeys = true, FailUpdateCount = 1 };
        var state = new MemoryRouteStateStore();
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10, 11] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            state, new StubRoutingClientFactory(api), utcNow: () => now);
        var result = service.RunOnceAsync().GetAwaiter().GetResult();
        Assert(result.FailedKeyCount == 1 && result.ChangedKeyCount == 1, "Partial Key failure was not reported per Key.");
        Assert(state.Load().CurrentGroupId is null, "Partial Key failure retained route certainty.");
    }

    internal static void TestAlreadyOptimalReportsSelectedKeys()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { KeysAlreadyOnTarget = true };
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
        var result = service.RunOnceAsync(dryRun: false).GetAwaiter().GetResult();
        Assert(result.KeyResults.Count == 1 && !result.KeyResults[0].Changed && result.KeyResults[0].Success,
            "Already-optimal cycle did not report its selected Key.");
    }

    internal static void TestMixedSelectedGroupsReconcile()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { TwoKeys = true, MixedGroups = true };
        var state = new MemoryRouteStateStore();
        state.Save(new RouteState { CurrentGroupId = 2 });
        var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10, 11] };
        using var service = new RoutingService(settings, new PersistentCredentials { BearerToken = "synthetic-access" },
            state, new StubRoutingClientFactory(api), utcNow: () => now);
        var result = service.RunOnceAsync().GetAwaiter().GetResult();
        Assert(api.UpdateCalls == 1 && result.ChangedKeyCount == 1,
            "A selected Key on a different group was not reconciled.");
        Assert(state.Load().CurrentGroupId == 2, "Mixed-group reconciliation lost the target state.");
    }

    internal static void TestActiveProbeKeyIsExcludedFromRouting()
    {
        var now = DateTimeOffset.UtcNow;
        var api = new StubRoutingClient(now) { TwoKeys = true };
        var settings = new PersistentAppSettings
        {
            BaseUrl = "https://example.test",
            KeySelectionInitialized = true,
            SelectedKeyIds = [10, 11],
            ActiveProbeEnabled = true,
            ActiveProbeKeyId = 10,
            ActiveProbeModel = "probe-model"
        };
        var upstream = new StubUpstreamProbeClient(request => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
        using var service = new RoutingService(
            settings,
            new PersistentCredentials
            {
                BearerToken = "synthetic-access",
                ActiveProbeApiKey = "probe-key-value"
            },
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(api),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(result.SelectedKeyIds.SequenceEqual(new long[] { 11 }),
            "The dedicated active-probe Key was included in normal routing.");
        Assert(result.KeyResults.Count == 1 && result.KeyResults[0].KeyId == 11,
            "Routing attempted to update the dedicated active-probe Key.");
        Assert(api.UpdatedGroupIds.SequenceEqual(new long[] { 2, 1, 2 }),
            "The dedicated probe Key was not restored before the remaining route Key was updated.");
    }

}
