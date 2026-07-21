using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Action Body)[]
{
    ("Bearer token normalization", TestBearerNormalization),
    ("Token extraction from cookie", TestCookieTokenExtraction),
    ("Lowest available authorized group", TestLowestAvailableGroup),
    ("User rate override", TestUserRateOverride),
    ("Availability threshold", TestAvailabilityThreshold),
    ("Provider warnings deserialize", TestProviderWarningsDeserialize),
    ("Warning provider remains eligible", TestWarningProviderRemainsEligible),
    ("Latest unavailable state remains ineligible", TestLatestUnavailableStateRemainsIneligible),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Routing preferences default to Win32-compatible values", TestRoutingPreferenceDefaults),
    ("Routing preferences roundtrip", TestRoutingPreferenceRoundtrip),
    ("Balanced mode buys meaningful latency", TestBalancedModeBuysLatency),
    ("Balanced mode keeps price for moderate speed gap", TestBalancedModeKeepsPriceForModerateSpeedGap),
    ("Economy mode protects price", TestEconomyModeProtectsPrice),
    ("Speed mode accepts larger price premium", TestSpeedModeAcceptsLargerPremium),
    ("Selective policy preserves local routing weights", TestSelectivePolicyPreservesLocalWeights),
    ("Close faster score keeps current group", TestCloseFasterScoreKeepsCurrentGroup),
    ("Close cheaper score keeps current group", TestCloseCheaperScoreKeepsCurrentGroup),
    ("Meaningful score advantage still switches", TestMeaningfulScoreAdvantageStillSwitches),
    ("Undefined score does not block a switch", TestUndefinedScoreDoesNotBlockSwitch),
    ("Missing latency ranks last", TestMissingLatencyRanksLast),
    ("Invalid measurements are excluded", TestInvalidMeasurementsAreExcluded),
    ("Extreme latency scores stay finite", TestExtremeLatencyScoresStayFinite),
    ("Zero multiplier remains free", TestZeroMultiplierWindow),
    ("Initial route has an explainable reason", TestInitialRouteDecision),
    ("Weighted speed winner switches immediately", TestWeightedSpeedWinnerSwitchesImmediately),
    ("Already optimal route does not switch", TestAlreadyOptimalRouteDecision),
    ("Invalid current route switches", TestInvalidCurrentRouteDecision),
    ("No candidate keeps route state", TestNoCandidateDecision),
    ("Route state persists atomically", TestRouteStateRoundtrip),
    ("Unreadable route state resets safely", TestUnreadableRouteStateResets),
    ("Dry run never updates a Key", TestDryRunNeverUpdatesKey),
    ("Account data is cached but monitor data is fresh", TestAccountDataCache),
    ("Routing result exposes cached user rates", TestRoutingResultExposesUserRates),
    ("Forced refresh bypasses account cache", TestForcedAccountRefresh),
    ("Business authentication failure retries once", TestBusinessAuthenticationRetry),
    ("Network failure never triggers login", TestRoutingNetworkFailureDoesNotLogin),
    ("Explicit empty Key selection is rejected", TestRoutingRejectsEmptySelection),
    ("Successful updates persist target state", TestSuccessfulRoutePersistsState),
    ("Partial update failure clears route certainty", TestPartialFailureClearsState),
    ("Already optimal cycle reports selected Keys", TestAlreadyOptimalReportsSelectedKeys),
    ("Mixed selected groups reconcile to target", TestMixedSelectedGroupsReconcile),
    ("Audit log writes valid JSON and rotates safely", TestAuditLogWritesValidJsonAndRotates),
    ("Publish script checks native exit codes", TestPublishScriptChecksNativeExitCodes),
    ("Encrypted settings roundtrip", TestEncryptedSettingsRoundtrip),
    ("Usable access token is reused", TestUsableAccessTokenIsReused),
    ("Expired access token refreshes first", TestExpiredAccessTokenRefreshesFirst),
    ("Rejected refresh falls back to login", TestRejectedRefreshFallsBackToLogin),
    ("Refresh API code falls back to login", TestRefreshApiCodeFallsBackToLogin),
    ("Refresh network failure does not log in", TestRefreshNetworkFailureDoesNotLogIn),
    ("Authentication API code is classified", TestAuthenticationApiCodeIsClassified),
    ("Login endpoint maps session", TestLoginEndpointMapsSession),
    ("Refresh endpoint maps rotated session", TestRefreshEndpointMapsRotatedSession),
    ("Refresh keeps token when server omits rotation", TestRefreshKeepsTokenWhenServerOmitsRotation),
    ("Authentication error hides server message", TestAuthenticationErrorHidesServerMessage),
    ("Business error hides server message", TestBusinessErrorHidesServerMessage),
    ("Interactive login requirement is rejected", TestInteractiveLoginRequirementIsRejected),
    ("Empty key selection roundtrips", TestEmptyKeySelectionRoundtrips),
    ("First key selection chooses first active key", TestFirstKeySelectionChoosesFirstActiveKey),
    ("Initialized empty key selection stays empty", TestInitializedEmptyKeySelectionStaysEmpty)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (Environment.GetEnvironmentVariable("AIHUB_SMOKE_TEST") == "1")
{
    try
    {
        using var client = new AIHubClient("https://aihub.top");
        var summary = await client.GetProviderSummaryAsync();
        Assert(summary.Apis.Count > 0, "Public provider endpoint returned no entries.");
        Console.WriteLine($"PASS Public API smoke test ({summary.Apis.Count} entries)");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL Public API smoke test: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void TestBearerNormalization()
{
    Assert(CredentialParser.NormalizeBearerToken("Authorization: Bearer abc.def") == "abc.def", "Header was not normalized.");
    Assert(CredentialParser.NormalizeBearerToken("Bearer token") == "token", "Bearer prefix was not removed.");
}

static void TestCookieTokenExtraction()
{
    var token = CredentialParser.TryExtractTokenFromCookie("theme=dark; auth_token=abc%2Edef; lang=zh");
    Assert(token == "abc.def", "auth_token cookie was not decoded.");
}

static void TestLowestAvailableGroup()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, available: false, success: 1, now),
        Provider(2, 0.04, available: true, success: 0.8, now),
        Provider(3, 0.03, available: true, success: 0.9, now)
    };
    var groups = new[] { Group(2), Group(3) };

    var result = RoutingEngine.SelectCheapest(providers, groups, new Dictionary<long, double>(), Criteria(), now);
    Assert(result?.Group.Id == 3, "Did not select the cheapest available authorized group.");
}

static void TestUserRateOverride()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.02, true, 1, now),
        Provider(2, 0.04, true, 1, now)
    };
    var rates = new Dictionary<long, double> { [1] = 0.10, [2] = 0.01 };

    var result = RoutingEngine.SelectCheapest(providers, new[] { Group(1), Group(2) }, rates, Criteria(), now);
    Assert(result?.Group.Id == 2 && result.HasUserRateOverride, "User rate override was not used.");
}

static void TestAvailabilityThreshold()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.01, true, 0.49, now),
        Provider(2, 0.05, true, 0.99, now)
    };
    var criteria = new RoutingCriteria("openai", 0.5, TimeSpan.FromMinutes(15));

    var result = RoutingEngine.SelectCheapest(providers, new[] { Group(1), Group(2) }, new Dictionary<long, double>(), criteria, now);
    Assert(result?.Group.Id == 2, "Low-availability group was not rejected.");
}

static void TestProviderWarningsDeserialize()
{
    var provider = JsonSerializer.Deserialize<ProviderStatus>("""
        {
          "id":"provider-1",
          "warningReasons":[{"type":"latency_spike","message":"synthetic warning","count":3}]
        }
        """)!;

    Assert(provider.HasWarnings, "Warning metadata was not recognized.");
    Assert(provider.WarningReasons.Single().Type == "latency_spike", "Warning type was not mapped.");
    Assert(provider.WarningReasons.Single().Count == 3, "Warning count was not mapped.");
}

static void TestWarningProviderRemainsEligible()
{
    var now = DateTimeOffset.UtcNow;
    var provider = Provider(1, 0.01, available: true, success: 0.95, now, warning: true);
    var result = RoutingEngine.SelectCheapest(
        [provider],
        [Group(1)],
        new Dictionary<long, double>(),
        new RoutingCriteria("openai", 0.9, TimeSpan.FromMinutes(15)),
        now);

    Assert(result?.Group.Id == 1 && result.Provider.HasWarnings,
        "Warning metadata incorrectly made an available provider ineligible.");
}

static void TestLatestUnavailableStateRemainsIneligible()
{
    var now = DateTimeOffset.UtcNow;
    var result = RoutingEngine.SelectCheapest(
        [
            Provider(1, 0.01, available: false, success: 1, now),
            Provider(2, 0.02, available: true, success: 0.95, now)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        new RoutingCriteria("openai", 0.9, TimeSpan.FromMinutes(15)),
        now);

    Assert(result?.Group.Id == 2, "Latest unavailable state did not control eligibility.");
}

static void TestStaleStatusRejection()
{
    var now = DateTimeOffset.UtcNow;
    var providers = new[]
    {
        Provider(1, 0.01, true, 1, now - TimeSpan.FromMinutes(16)),
        Provider(2, 0.05, true, 1, now)
    };

    var result = RoutingEngine.SelectCheapest(providers, new[] { Group(1), Group(2) }, new Dictionary<long, double>(), Criteria(), now);
    Assert(result?.Group.Id == 2, "Stale provider status was not rejected.");
}

static void TestRoutingPreferenceDefaults()
{
    var settings = new PersistentAppSettings();
    Assert(settings.RoutingMode == RoutingMode.Economy, "New installs must preserve lowest-price routing.");
    Assert(settings.AccountCacheSeconds == 300, "Account cache default changed.");
    Assert(settings.Theme == WinFormsTheme.System, "Theme must follow Windows by default.");
}

static void TestRoutingPreferenceRoundtrip()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(directory);
        store.Save(new PersistentAppSettings
        {
            RoutingMode = RoutingMode.Speed,
            AccountCacheSeconds = 90,
            Theme = WinFormsTheme.Dark
        }, null);

        var loaded = store.Load().Settings;
        Assert(loaded.RoutingMode == RoutingMode.Speed, "Routing mode did not roundtrip.");
        Assert(loaded.AccountCacheSeconds == 90, "Cache duration did not roundtrip.");
        Assert(loaded.Theme == WinFormsTheme.Dark, "Theme did not roundtrip.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestBalancedModeBuysLatency()
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

static void TestBalancedModeKeepsPriceForModerateSpeedGap()
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

static void TestEconomyModeProtectsPrice()
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

static void TestSpeedModeAcceptsLargerPremium()
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

static void TestSelectivePolicyPreservesLocalWeights()
{
    Assert(Policy(RoutingMode.Economy).PriceWeight == 0.95, "Economy weight changed.");
    Assert(Policy(RoutingMode.Balanced).PriceWeight == 0.80, "Balanced weight changed.");
    Assert(Policy(RoutingMode.Speed).PriceWeight == 0.35, "Speed weight changed.");
    Assert(new PersistentAppSettings().RoutingMode == RoutingMode.Economy, "Default mode changed.");
    Assert(Policy(RoutingMode.Balanced).MinimumScoreAdvantageToSwitch == 0.05,
        "Stability threshold changed.");
}

static void TestCloseFasterScoreKeepsCurrentGroup()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        [Provider(1, 0.02, true, 0.99, now, 1_000), Provider(2, 0.02, true, 0.99, now, 980)],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now);
    Assert(evaluation.Recommended?.Group.Id == 2, "Test setup did not recommend the slightly faster route.");

    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);
    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "A tiny speed advantage replaced the current group.");
    Assert(result.Decision.Reason == RouteDecisionReason.ScoreAdvantageTooSmall,
        "A held speed decision did not expose its stability reason.");
}

static void TestCloseCheaperScoreKeepsCurrentGroup()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        [Provider(1, 0.0201, true, 0.99, now, 981), Provider(2, 0.02, true, 0.99, now, 1_000)],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now);
    Assert(evaluation.Recommended?.Group.Id == 2, "Test setup did not recommend the slightly cheaper route.");

    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);
    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "A tiny price advantage replaced the current group.");
    Assert(result.Decision.Reason == RouteDecisionReason.ScoreAdvantageTooSmall,
        "A held price decision did not expose its stability reason.");
}

static void TestMeaningfulScoreAdvantageStillSwitches()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Balanced);
    var evaluation = RoutingEngine.Evaluate(
        [Provider(1, 0.02, true, 0.99, now, 1_000), Provider(2, 0.02, true, 0.99, now, 400)],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);
    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "A meaningful score advantage was blocked.");
}

static void TestUndefinedScoreDoesNotBlockSwitch()
{
    var now = DateTimeOffset.UtcNow;
    var policy = Policy(RoutingMode.Economy);
    var evaluation = RoutingEngine.Evaluate(
        [Provider(1, 0.01, true, 0.99, now, 1_000), Provider(2, 0, true, 0.99, now, 2_000)],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        policy,
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        policy,
        now,
        observedCurrentGroupId: 1);
    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "An undefined weighted score blocked a zero-price route.");
}

static void TestMissingLatencyRanksLast()
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

static void TestInvalidMeasurementsAreExcluded()
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

static void TestExtremeLatencyScoresStayFinite()
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

static void TestZeroMultiplierWindow()
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

static void TestInitialRouteDecision()
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

static void TestWeightedSpeedWinnerSwitchesImmediately()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [Provider(1, 0.02, true, 0.99, now, 10_000), Provider(2, 0.021, true, 0.99, now, 1_000)],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        Policy(RoutingMode.Balanced),
        now,
        1);
    Assert(result.Decision.ShouldSwitch && result.Decision.Reason == RouteDecisionReason.FasterForWeightedTradeoff,
        "A clear weighted speed winner did not switch immediately.");
}

static void TestAlreadyOptimalRouteDecision()
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

static void TestInvalidCurrentRouteDecision()
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

static void TestNoCandidateDecision()
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

static void TestRouteStateRoundtrip()
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

static void TestUnreadableRouteStateResets()
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

static void TestDryRunNeverUpdatesKey()
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

static void TestAccountDataCache()
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

static void TestRoutingResultExposesUserRates()
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

static void TestForcedAccountRefresh()
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

static void TestBusinessAuthenticationRetry()
{
    var now = DateTimeOffset.UtcNow;
    var api = new StubRoutingClient(now) { FailFirstSummaryAuth = true };
    var settings = new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] };
    using var service = new RoutingService(settings, new PersistentCredentials
    {
        Email = "user@example.test",
        Password = "synthetic-password",
        BearerToken = "synthetic-old-access",
        RefreshToken = "synthetic-refresh",
        AccessTokenExpiresAt = now.AddHours(1)
    }, new MemoryRouteStateStore(), new StubRoutingClientFactory(api), utcNow: () => now);
    var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
    Assert(result.DryRun && api.SummaryCalls == 2, "Business authentication failure was not retried once.");
    Assert(api.RefreshCalls == 1 && api.LoginCalls == 0, "Retry did not refresh before password login.");
}

static void TestRoutingNetworkFailureDoesNotLogin()
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

static void TestRoutingRejectsEmptySelection()
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

static void TestSuccessfulRoutePersistsState()
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

static void TestPartialFailureClearsState()
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

static void TestAlreadyOptimalReportsSelectedKeys()
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

static void TestMixedSelectedGroupsReconcile()
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

static void TestAuditLogWritesValidJsonAndRotates()
{
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var path = Path.Combine(directory, "routing.jsonl");
        var writer = new AuditLogWriter(path, maximumBytes: 512, retainedFiles: 2);
        var entry = new RouteAuditEntry(
            DateTimeOffset.UtcNow,
            RoutingMode.Balanced,
            RouteDecisionReason.FasterForWeightedTradeoff,
            1,
            2,
            false,
            [
                new RouteAuditCandidate(2, 0.02, 250, 0.4, true),
                new RouteAuditCandidate(3, double.NaN, double.PositiveInfinity, double.NegativeInfinity, false)
            ],
            [new RouteAuditKey(10, true, true, null)]);
        writer.Write(entry);
        writer.Write(entry);

        Assert(File.Exists(path), "Audit log was not created.");
        Assert(File.Exists(path + ".1"), "Audit log did not rotate at the configured size.");
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line);
                Assert(document.RootElement.TryGetProperty("timestamp", out _), "Audit JSON omitted timestamp.");
                Assert(!line.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("refresh", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("userAgent", StringComparison.OrdinalIgnoreCase),
                    "Audit JSON contained a sensitive property name.");
            }
        }
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void TestPublishScriptChecksNativeExitCodes()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "publish.ps1")))
    {
        directory = directory.Parent;
    }

    Assert(directory is not null, "Repository root was not found from the test output directory.");
    var script = File.ReadAllText(Path.Combine(directory!.FullName, "scripts", "publish.ps1"));
    Assert(script.Contains("function Invoke-DotNet", StringComparison.Ordinal),
        "Publish script has no checked dotnet wrapper.");
    Assert(!script.Split('\n').Any(line => line.TrimStart().StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)),
        "Publish script contains an unchecked dotnet command.");
}

static void TestEncryptedSettingsRoundtrip()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    const string secretToken = "unit-test-secret-token";
    try
    {
        var store = new AppSettingsStore(directory);
        var settings = new PersistentAppSettings
        {
            PersistCredentials = true,
            BaseUrl = "https://example.test",
            Platform = "openai",
            MinimumSuccessPercent = 85,
            PollingIntervalSeconds = 120,
            SmoothRendering = true,
            KeySelectionInitialized = true,
            SelectedKeyIds = [42, 84]
        };
        var expiresAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var credentials = new PersistentCredentials
        {
            Email = "distribution-test@example.test",
            Password = "unit-test-password",
            BearerToken = secretToken,
            RefreshToken = "unit-test-refresh-token",
            AccessTokenExpiresAt = expiresAt,
            Cookie = "session=secret-cookie",
            UserAgent = "test-user-agent"
        };

        store.Save(settings, credentials);
        var encrypted = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));
        var encryptedText = Encoding.UTF8.GetString(encrypted);
        Assert(!encryptedText.Contains(secretToken, StringComparison.Ordinal), "Credential file contains plaintext access token.");
        Assert(!encryptedText.Contains(credentials.RefreshToken, StringComparison.Ordinal), "Credential file contains plaintext refresh token.");
        Assert(!encryptedText.Contains(credentials.Password, StringComparison.Ordinal), "Credential file contains plaintext password.");
        Assert(!encryptedText.Contains(credentials.Email, StringComparison.Ordinal), "Credential file contains plaintext email.");
        Assert(!encryptedText.Contains(credentials.Cookie, StringComparison.Ordinal), "Credential file contains plaintext Cookie.");
        Assert(!encryptedText.Contains(credentials.UserAgent, StringComparison.Ordinal), "Credential file contains plaintext User-Agent.");
        var settingsText = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Assert(!settingsText.Contains(credentials.Email, StringComparison.Ordinal), "Plain settings contain the login email.");
        Assert(!settingsText.Contains(credentials.Password, StringComparison.Ordinal), "Plain settings contain the password.");
        Assert(!settingsText.Contains(secretToken, StringComparison.Ordinal), "Plain settings contain the access token.");
        Assert(!settingsText.Contains(credentials.RefreshToken, StringComparison.Ordinal), "Plain settings contain the refresh token.");

        var loaded = store.Load();
        Assert(loaded.Settings.PersistCredentials, "Persistence flag was not restored.");
        Assert(loaded.Settings.PollingIntervalSeconds == 120, "Polling interval was not restored.");
        Assert(loaded.Settings.KeySelectionInitialized, "Key selection initialized state was not restored.");
        Assert(loaded.Settings.SelectedKeyIds.SequenceEqual(new long[] { 42, 84 }), "Selected Key IDs were not restored.");
        Assert(loaded.Credentials?.Email == credentials.Email, "Encrypted email did not roundtrip.");
        Assert(loaded.Credentials?.Password == credentials.Password, "Encrypted password did not roundtrip.");
        Assert(loaded.Credentials?.BearerToken == secretToken, "Encrypted token did not roundtrip.");
        Assert(loaded.Credentials?.RefreshToken == credentials.RefreshToken, "Encrypted refresh token did not roundtrip.");
        Assert(loaded.Credentials?.AccessTokenExpiresAt == expiresAt, "Access token expiration did not roundtrip.");
        Assert(loaded.Credentials?.Cookie == credentials.Cookie, "Encrypted cookie did not roundtrip.");

        store.Save(new PersistentAppSettings { PersistCredentials = false }, null);
        Assert(!File.Exists(Path.Combine(directory, "credentials.dat")), "Credential file was not removed after disabling persistence.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestUsableAccessTokenIsReused()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    var persistCalls = 0;
    var existing = new AuthSession("access-current", "refresh-current", now.AddMinutes(10));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            return Task.FromResult(new AuthSession("access-refreshed", "refresh-refreshed", now.AddHours(1)));
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", now.AddHours(1)));
        },
        (session, cancellationToken) =>
        {
            persistCalls++;
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        existing,
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, existing), "Coordinator did not reuse the current session instance.");
    Assert(refreshCalls == 0, "Refresh was called for a usable access token.");
    Assert(loginCalls == 0, "Login was called for a usable access token.");
    Assert(persistCalls == 0, "Unchanged session was persisted unnecessarily.");
}

static void TestExpiredAccessTokenRefreshesFirst()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    AuthSession? persisted = null;
    var refreshed = new AuthSession("access-refreshed", "refresh-rotated", now.AddHours(1));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            Assert(refreshToken == "refresh-current", "Coordinator passed the wrong refresh token.");
            return Task.FromResult(refreshed);
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", now.AddHours(1)));
        },
        (session, cancellationToken) =>
        {
            persisted = session;
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-current", now.AddSeconds(-1)),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, refreshed), "Coordinator did not return the refreshed session.");
    Assert(refreshCalls == 1, "Refresh was not called exactly once.");
    Assert(loginCalls == 0, "Login was called after a successful refresh.");
    Assert(ReferenceEquals(persisted, refreshed), "Rotated refresh token was not persisted.");
}

static void TestRejectedRefreshFallsBackToLogin()
{
    var now = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    var refreshCalls = 0;
    var loginCalls = 0;
    var persistCalls = 0;
    var loggedIn = new AuthSession("access-login", "refresh-login", now.AddHours(1));
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) =>
        {
            refreshCalls++;
            throw new AIHubApiException("Refresh rejected.", HttpStatusCode.Unauthorized, "INVALID_TOKEN");
        },
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            Assert(credentials.Email == "user@example.test", "Coordinator passed the wrong email.");
            Assert(credentials.Password == "password", "Coordinator passed the wrong password.");
            return Task.FromResult(loggedIn);
        },
        (session, cancellationToken) =>
        {
            persistCalls++;
            Assert(ReferenceEquals(session, loggedIn), "Coordinator persisted the rejected session.");
            return Task.CompletedTask;
        },
        () => now);

    var result = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-rejected", now.AddMinutes(-5)),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(ReferenceEquals(result, loggedIn), "Coordinator did not return the login session.");
    Assert(refreshCalls == 1, "Rejected refresh was not attempted exactly once.");
    Assert(loginCalls == 1, "Login fallback was not attempted exactly once.");
    Assert(persistCalls == 1, "Login session was not persisted exactly once.");
}

static void TestRefreshApiCodeFallsBackToLogin()
{
    var handler = new StubHttpMessageHandler(request => JsonResponse("""
        {"code":"invalid_grant","message":"refresh rejected","data":null}
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);
    var loginCalls = 0;
    var coordinator = new SessionCoordinator(
        client.RefreshSessionAsync,
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", DateTimeOffset.UtcNow.AddHours(1)));
        },
        (session, cancellationToken) => Task.CompletedTask);

    var session = coordinator.GetSessionAsync(
        new AuthSession("access-expired", "refresh-rejected", DateTimeOffset.MinValue),
        new LoginCredentials("user@example.test", "password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(loginCalls == 1, "HTTP 200 invalid_grant did not trigger login fallback.");
    Assert(session.AccessToken == "access-login", "Login fallback session was not returned.");
}

static void TestAuthenticationApiCodeIsClassified()
{
    var exception = new AIHubApiException("Synthetic auth failure.", HttpStatusCode.OK, "401");
    Assert(exception.IsAuthenticationFailure, "API code 401 was not classified as an authentication failure.");
}

static void TestRefreshNetworkFailureDoesNotLogIn()
{
    var loginCalls = 0;
    var coordinator = new SessionCoordinator(
        (refreshToken, cancellationToken) => throw new HttpRequestException("Synthetic network failure."),
        (credentials, cancellationToken) =>
        {
            loginCalls++;
            return Task.FromResult(new AuthSession("access-login", "refresh-login", DateTimeOffset.UtcNow.AddHours(1)));
        },
        (session, cancellationToken) => Task.CompletedTask);

    try
    {
        coordinator.GetSessionAsync(
            new AuthSession("access-expired", "refresh-current", DateTimeOffset.MinValue),
            new LoginCredentials("user@example.test", "password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Network failure was swallowed.");
    }
    catch (HttpRequestException)
    {
        Assert(loginCalls == 0, "Network failure incorrectly triggered password login.");
    }
}

static void TestLoginEndpointMapsSession()
{
    var now = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.Method == HttpMethod.Post, "Login did not use POST.");
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/login", "Login used the wrong endpoint.");
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert(body.Contains("user@example.test", StringComparison.Ordinal), "Login request omitted the email.");
        Assert(body.Contains("synthetic-password", StringComparison.Ordinal), "Login request omitted the password.");
        return JsonResponse("""
            {"code":0,"message":"ok","data":{"access_token":"access-login","refresh_token":"refresh-login","expires_in":3600,"token_type":"Bearer","user":{"email":"user@example.test"}}}
            """);
    });
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        utcNow: () => now);

    var session = client.LoginAsync(
        new LoginCredentials("user@example.test", "synthetic-password"),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.AccessToken == "access-login", "Login access token was not mapped.");
    Assert(session.RefreshToken == "refresh-login", "Login refresh token was not mapped.");
    Assert(session.ExpiresAt == now.AddSeconds(3600), "Login expiration was not converted to an absolute time.");
}

static void TestRefreshEndpointMapsRotatedSession()
{
    var now = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
    var handler = new StubHttpMessageHandler(request =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/api/v1/auth/refresh", "Refresh used the wrong endpoint.");
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert(body.Contains("refresh-old", StringComparison.Ordinal), "Refresh request omitted the refresh token.");
        return JsonResponse("""
            {"code":0,"message":"ok","data":{"access_token":"access-new","refresh_token":"refresh-new","expires_in":1800,"token_type":"Bearer"}}
            """);
    });
    using var client = new AIHubClient(
        "https://example.test",
        messageHandler: handler,
        utcNow: () => now);

    var session = client.RefreshSessionAsync("refresh-old", CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.AccessToken == "access-new", "Refreshed access token was not mapped.");
    Assert(session.RefreshToken == "refresh-new", "Rotated refresh token was not mapped.");
    Assert(session.ExpiresAt == now.AddSeconds(1800), "Refresh expiration was not converted to an absolute time.");
}

static void TestRefreshKeepsTokenWhenServerOmitsRotation()
{
    var handler = new StubHttpMessageHandler(request => JsonResponse("""
        {"code":0,"message":"ok","data":{"access_token":"access-new","expires_in":1800,"token_type":"Bearer"}}
        """));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    var session = client.RefreshSessionAsync("refresh-current", CancellationToken.None).GetAwaiter().GetResult();

    Assert(session.RefreshToken == "refresh-current", "Refresh discarded the existing token when no rotation was returned.");
}

static void TestAuthenticationErrorHidesServerMessage()
{
    const string sensitiveMessage = "synthetic-email@example.test synthetic-temporary-token";
    var handler = new StubHttpMessageHandler(request => JsonResponse(
        "{\"code\":\"invalid_grant\",\"message\":\"" + sensitiveMessage + "\",\"data\":null}"));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.RefreshSessionAsync("refresh-current", CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected refresh was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "invalid_grant", "Authentication error discarded the safe API code.");
        Assert(!exception.Message.Contains(sensitiveMessage, StringComparison.Ordinal), "Authentication error exposed the server message.");
    }
}

static void TestBusinessErrorHidesServerMessage()
{
    const string sensitiveMessage = "synthetic-cookie=session-value synthetic-key=sk-secret";
    var handler = new StubHttpMessageHandler(request => JsonResponse(
        "{\"code\":\"500\",\"message\":\"" + sensitiveMessage + "\",\"data\":null}"));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Rejected business response was accepted.");
    }
    catch (AIHubApiException exception)
    {
        Assert(exception.ApiCode == "500", "Business error discarded the API code.");
        Assert(!exception.Message.Contains(sensitiveMessage, StringComparison.Ordinal), "Business error exposed the server message.");
    }
}

static void TestInteractiveLoginRequirementIsRejected()
{
    const string temporaryToken = "temporary-two-factor-token-must-not-leak";
    var responseJson = "{\"code\":0,\"message\":\"ok\",\"data\":{\"requires_2fa\":true,\"temp_token\":\"" +
        temporaryToken +
        "\",\"user_email_masked\":\"u***@example.test\"}}";
    var handler = new StubHttpMessageHandler(request => JsonResponse(responseJson));
    using var client = new AIHubClient("https://example.test", messageHandler: handler);

    try
    {
        client.LoginAsync(
            new LoginCredentials("user@example.test", "synthetic-password"),
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Interactive authentication response was accepted.");
    }
    catch (InteractiveAuthenticationRequiredException exception)
    {
        Assert(!exception.Message.Contains(temporaryToken, StringComparison.Ordinal), "Interactive auth error leaked the temporary token.");
    }
}

static void TestEmptyKeySelectionRoundtrips()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(directory);
        store.Save(new PersistentAppSettings
        {
            PersistCredentials = false,
            KeySelectionInitialized = true,
            SelectedKeyIds = []
        }, null);

        var loaded = store.Load();
        Assert(loaded.Settings.KeySelectionInitialized, "Explicit empty selection lost its initialized state.");
        Assert(loaded.Settings.SelectedKeyIds.Length == 0, "Explicit empty selection was not preserved.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestFirstKeySelectionChoosesFirstActiveKey()
{
    var selected = KeySelectionPolicy.Resolve(
        initialized: false,
        savedIds: [],
        keys:
        [
            new ApiKeyInfo { Id = 10, Status = "disabled" },
            new ApiKeyInfo { Id = 20, Status = "active" },
            new ApiKeyInfo { Id = 30, Status = "active" }
        ]);

    Assert(selected.SequenceEqual(new long[] { 20 }), "First load did not select only the first active Key.");
}

static void TestInitializedEmptyKeySelectionStaysEmpty()
{
    var keys = new[]
    {
        new ApiKeyInfo { Id = 10, Status = "active" },
        new ApiKeyInfo { Id = 20, Status = "active" }
    };
    var empty = KeySelectionPolicy.Resolve(initialized: true, savedIds: [], keys);
    var restored = KeySelectionPolicy.Resolve(initialized: true, savedIds: [20, 999], keys);

    Assert(empty.Count == 0, "An initialized empty selection selected a Key again.");
    Assert(restored.SequenceEqual(new long[] { 20 }), "Saved selection did not ignore unavailable Key IDs.");
}

static HttpResponseMessage JsonResponse(string json)
{
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

static ProviderStatus Provider(
    long groupId,
    double rate,
    bool available,
    double success,
    DateTimeOffset checkedAt,
    double? latency = 1000,
    bool warning = false)
{
    return new ProviderStatus
    {
        Id = $"provider-{groupId}",
        GroupId = groupId,
        PlanType = $"Plan {groupId}",
        Platform = "openai",
        PriceMultiplier = rate,
        Available = available,
        Enabled = true,
        CheckedAt = checkedAt,
        FirstTokenLatencyMs = latency,
        SuccessRates = new Dictionary<string, double> { ["6h"] = success },
        WarningReasons = warning
            ? [new ProviderWarningReason { Type = "synthetic_warning", Message = "synthetic warning" }]
            : []
    };
}

static GroupInfo Group(long id)
{
    return new GroupInfo
    {
        Id = id,
        Name = $"Group {id}",
        Platform = "openai",
        RateMultiplier = 1,
        Status = "active"
    };
}

static RoutingCriteria Criteria() => new("openai", 0, TimeSpan.FromMinutes(15));

static BalancedRoutingPolicy Policy(RoutingMode mode) => new()
{
    Platform = "openai",
    Mode = mode,
    MinimumSuccessRate6h = 0,
    MaximumStatusAge = TimeSpan.FromMinutes(15)
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

sealed class MemoryRouteStateStore : IRouteStateStore
{
    private RouteState _state = new();
    public RouteState Load() => _state;
    public void Save(RouteState state) => _state = state;
}

sealed class StubRoutingClientFactory(IAIHubApiClient client) : IAIHubClientFactory
{
    public IAIHubApiClient Create(string baseUrl, string? bearerToken, string? cookie, string? userAgent) => client;
}

sealed class StubRoutingClient(DateTimeOffset now) : IAIHubApiClient
{
    public int SummaryCalls { get; private set; }
    public int GroupsCalls { get; private set; }
    public int RatesCalls { get; private set; }
    public int KeysCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public int LoginCalls { get; private set; }
    public bool FailFirstSummaryAuth { get; init; }
    public bool ThrowNetwork { get; init; }
    public bool TwoKeys { get; init; }
    public bool KeysAlreadyOnTarget { get; init; }
    public bool MixedGroups { get; init; }
    public int FailUpdateCount { get; init; }
    public double? UserRateOverride { get; init; }

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default)
    {
        SummaryCalls++;
        if (ThrowNetwork) throw new HttpRequestException("synthetic network failure");
        if (FailFirstSummaryAuth && SummaryCalls == 1)
            throw new AIHubApiException("Authentication required.", HttpStatusCode.Unauthorized, "401");
        return Task.FromResult(new MonitorSummary
        {
            Apis = [ProviderForStub(2, now)]
        });
    }

    public Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        LoginCalls++;
        return Task.FromResult(new AuthSession("synthetic-login", "synthetic-refresh-login", now.AddHours(1)));
    }

    public Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return Task.FromResult(new AuthSession("synthetic-refreshed", "synthetic-refresh-rotated", now.AddHours(1)));
    }

    public Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default)
    {
        GroupsCalls++;
        return Task.FromResult<IReadOnlyList<GroupInfo>>([GroupForStub(2)]);
    }

    public Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default)
    {
        RatesCalls++;
        IReadOnlyDictionary<long, double> rates = UserRateOverride is { } value
            ? new Dictionary<long, double> { [2] = value }
            : new Dictionary<long, double>();
        return Task.FromResult(rates);
    }

    public Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default)
    {
        KeysCalls++;
        var keys = new List<ApiKeyInfo>
        {
            new() { Id = 10, Name = "Synthetic Key 10", Status = "active", GroupId = KeysAlreadyOnTarget ? 2 : 1 }
        };
        if (TwoKeys)
        {
            keys.Add(new ApiKeyInfo
            {
                Id = 11,
                Name = "Synthetic Key 11",
                Status = "active",
                GroupId = KeysAlreadyOnTarget || !MixedGroups ? (KeysAlreadyOnTarget ? 2 : 1) : 2
            });
        }

        return Task.FromResult<IReadOnlyList<ApiKeyInfo>>(keys);
    }

    public Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default)
    {
        UpdateCalls++;
        if (UpdateCalls <= FailUpdateCount)
        {
            throw new InvalidOperationException("synthetic update failure");
        }

        return Task.FromResult(new ApiKeyInfo
        {
            Id = keyId,
            Name = $"Synthetic Key {keyId}",
            Status = "active",
            GroupId = groupId,
            Group = GroupForStub(groupId)
        });
    }

    private static ProviderStatus ProviderForStub(long groupId, DateTimeOffset checkedAt) => new()
    {
        Id = $"provider-{groupId}",
        GroupId = groupId,
        PlanType = "Synthetic",
        Platform = "openai",
        PriceMultiplier = 0.01,
        Available = true,
        Enabled = true,
        CheckedAt = checkedAt,
        FirstTokenLatencyMs = 500,
        SuccessRates = new Dictionary<string, double> { ["6h"] = 1 }
    };

    private static GroupInfo GroupForStub(long id) => new()
    {
        Id = id,
        Name = $"Group {id}",
        Platform = "openai",
        RateMultiplier = 1,
        Status = "active"
    };

    public void Dispose()
    {
    }
}
