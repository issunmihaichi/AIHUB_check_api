using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
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
            store.Save(new RouteState { CurrentGroupId = 42 });
            Assert(store.Load().CurrentGroupId == 42, "Route state did not roundtrip.");
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

}
