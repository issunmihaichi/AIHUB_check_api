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
    ("Null provider warnings are tolerated", TestNullProviderWarningsAreTolerated),
    ("Provider last-call aliases deserialize", TestProviderLastCallAliasesDeserialize),
    ("Adaptive constants match supplied economics", TestAdaptiveConstants),
    ("Adaptive preference follows interval boundaries", TestAdaptivePreferenceBoundaries),
    ("Current-group interval uses latest provider call", TestCurrentGroupIntervalResolution),
    ("Missing call time retains base preference", TestMissingCallTimeRetainsBasePreference),
    ("Adaptive penalty uses new multiplier", TestAdaptivePenalty),
    ("Adaptive completion time includes TTFT", TestAdaptiveCompletionTime),
    ("Adaptive net saving subtracts context penalty", TestAdaptiveNetSaving),
    ("Adaptive cost accepts positive saving", TestAdaptiveCostAcceptsPositiveSaving),
    ("Adaptive cost rejects slow candidate", TestAdaptiveCostRejectsSlowCandidate),
    ("Adaptive balanced requires all safeguards", TestAdaptiveBalancedSafeguards),
    ("Adaptive speed accepts generation boost", TestAdaptiveSpeedAcceptsGenerationBoost),
    ("Adaptive speed accepts end-to-end gain", TestAdaptiveSpeedAcceptsEndToEndGain),
    ("Adaptive short task is protected outside cost", TestAdaptiveShortTaskProtection),
    ("Adaptive invalid performance cannot switch", TestAdaptiveInvalidPerformance),
    ("Adaptive invalid old performance cannot satisfy relative time", TestAdaptiveInvalidOldPerformance),
    ("Warning provider remains eligible", TestWarningProviderRemainsEligible),
    ("Latest unavailable state remains ineligible", TestLatestUnavailableStateRemainsIneligible),
    ("Warning presentation excludes server message", TestWarningPresentationExcludesServerMessage),
    ("Warning decoration requires routable latest state", TestWarningDecorationRequiresRoutableLatestState),
    ("Routing presentation preserves availability threshold", TestRoutingPresentationPreservesAvailabilityThreshold),
    ("Routing presentation rejects invalid effective rate", TestRoutingPresentationRejectsInvalidEffectiveRate),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Routing preferences default to Win32-compatible values", TestRoutingPreferenceDefaults),
    ("Routing preferences roundtrip", TestRoutingPreferenceRoundtrip),
    ("Balanced mode buys meaningful latency", TestBalancedModeBuysLatency),
    ("Balanced mode keeps price for moderate speed gap", TestBalancedModeKeepsPriceForModerateSpeedGap),
    ("Economy mode protects price", TestEconomyModeProtectsPrice),
    ("Speed mode accepts larger price premium", TestSpeedModeAcceptsLargerPremium),
    ("Selective policy preserves local routing weights", TestSelectivePolicyPreservesLocalWeights),
    ("Cost mode proposes strict cheapest candidate", TestCostModeProposesCheapest),
    ("Cost mode falls back when cheapest TTFT is unknown", TestCostModeFallsBackWhenCheapestTtftIsUnknown),
    ("Frequent calls override economy with speed", TestFrequentCallsOverrideEconomy),
    ("Idle calls override speed with cost", TestIdleCallsOverrideSpeed),
    ("Adaptive rejection keeps current group", TestAdaptiveRejectionKeepsCurrentGroup),
    ("Adaptive acceptance updates selected Keys", TestAdaptiveAcceptanceUpdatesKeys),
    ("Adaptive traversal finds an accepted candidate beyond weighted winner", TestAdaptiveTraversalFindsAcceptedCandidateBeyondWeightedWinner),
    ("Initial and invalid routes recover immediately", TestAdaptiveRecoveryBypassesGuard),
    ("Missing latency ranks last", TestMissingLatencyRanksLast),
    ("Invalid measurements are excluded", TestInvalidMeasurementsAreExcluded),
    ("Extreme latency scores stay finite", TestExtremeLatencyScoresStayFinite),
    ("Zero multiplier remains free", TestZeroMultiplierWindow),
    ("Initial route has an explainable reason", TestInitialRouteDecision),
    ("Adaptive speed winner switches after guard", TestWeightedSpeedWinnerSwitchesImmediately),
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
    ("Authentication statuses have safe diagnostics", TestAuthenticationStatusesHaveSafeDiagnostics),
    ("Business authentication statuses keep business diagnostics", TestBusinessAuthenticationStatusesKeepBusinessDiagnostics),
    ("Malformed authentication responses retain endpoint context", TestMalformedAuthenticationResponsesRetainEndpointContext),
    ("Unknown errors do not expose credential text", TestUnknownErrorsDoNotExposeCredentialText),
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

static void TestNullProviderWarningsAreTolerated()
{
    var provider = JsonSerializer.Deserialize<ProviderStatus>("""
        {"id":"provider-1","warningReasons":null}
        """)!;
    Assert(!provider.HasWarnings, "A null warning list was not treated as empty.");
}

static void TestProviderLastCallAliasesDeserialize()
{
    var providers = JsonSerializer.Deserialize<ProviderStatus[]>("""
        [
          {
            "id":"preferred",
            "lastCallEndedAt":"2026-07-21T10:00:00Z",
            "lastCallAt":"2026-07-21T09:00:00Z"
          },
          {
            "id":"compatible",
            "lastCallAt":"2026-07-21T08:00:00Z"
          }
        ]
        """)!;

    Assert(providers[0].ResolvedLastCallEndedAt == DateTimeOffset.Parse("2026-07-21T10:00:00Z"),
        "lastCallEndedAt did not take precedence over the compatibility alias.");
    Assert(providers[1].ResolvedLastCallEndedAt == DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
        "lastCallAt was not accepted as a compatibility alias.");
}

static void TestAdaptiveConstants()
{
    Assert(AdaptiveRoutingConstants.InputPricePerMillion == 5.0, "Input price constant changed.");
    Assert(AdaptiveRoutingConstants.OutputPricePerMillion == 30.0, "Output price constant changed.");
    Assert(AdaptiveRoutingConstants.PenaltyTokens == 300_000, "Penalty token constant changed.");

    var shortConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Short);
    var mediumConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Medium);
    var longConfig = AdaptiveRoutingConstants.Duration(TaskDurationCategory.Long);
    Assert(shortConfig == new DurationConfiguration(0, 78_480, 3_600), "Short duration config changed.");
    Assert(mediumConfig == new DurationConfiguration(78_480, 313_920, 7_200), "Medium duration config changed.");
    Assert(longConfig == new DurationConfiguration(313_920, 1_883_520, 21_600), "Long duration config changed.");
}

static void TestAdaptivePreferenceBoundaries()
{
    Assert(AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(4.999, AdaptivePreference.Cost) == AdaptivePreference.Speed,
        "An interval below five seconds did not force Speed.");
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

static void TestCurrentGroupIntervalResolution()
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

static void TestMissingCallTimeRetainsBasePreference()
{
    var preference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
        currentIntervalSeconds: null,
        basePreference: AdaptivePreference.Balanced);
    Assert(preference == AdaptivePreference.Balanced, "A missing call timestamp fabricated an interval override.");
}

static void TestAdaptivePenalty()
{
    var penalty = AdaptiveSwitchDecisionEngine.CalculatePenalty(0.02);
    Assert(Math.Abs(penalty - 0.03) < 1e-12, "The context-miss penalty did not use the new multiplier.");
}

static void TestAdaptiveCompletionTime()
{
    var completion = AdaptiveSwitchDecisionEngine.CalculateCompletionTime(2, 20, 100);
    Assert(Math.Abs(completion - 7) < 1e-12, "TTFT was not included in completion time.");
    Assert(double.IsPositiveInfinity(AdaptiveSwitchDecisionEngine.CalculateCompletionTime(2, 0, 100)),
        "A non-positive generation speed did not produce infinite completion time.");
}

static void TestAdaptiveNetSaving()
{
    var saving = AdaptiveSwitchDecisionEngine.CalculateNetSaving(0.02, 0.01, 313_920);
    Assert(Math.Abs(saving - 0.079176) < 1e-12, "Net saving did not subtract the context penalty.");
    Assert(double.IsNegativeInfinity(AdaptiveSwitchDecisionEngine.CalculateNetSaving(0.01, 0.01, 313_920)),
        "A non-lower price did not produce negative infinite saving.");
}

static void TestAdaptiveCostAcceptsPositiveSaving()
{
    var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
        0.02, 0.01, 1, 1, 20, 20,
        TaskDurationCategory.Short, AdaptivePreference.Cost, 31));

    Assert(result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.AcceptedCost,
        "Cost mode rejected a positive net saving within the completion cap.");
    Assert(result.RemainingTokens == 78_480 && result.NetSavingUsd > 0,
        "Cost mode did not use the optimistic Short token estimate.");
}

static void TestAdaptiveCostRejectsSlowCandidate()
{
    var exactBoundarySpeed = 78_480d / AdaptiveRoutingConstants.MaximumCostCompletionSeconds;
    var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
        0.02, 0.01, 0, 0, 20, exactBoundarySpeed,
        TaskDurationCategory.Short, AdaptivePreference.Cost, 31));

    Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.CostGuardRejected,
        "Cost mode accepted a candidate at the strict 24-hour boundary.");
}

static void TestAdaptiveBalancedSafeguards()
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

static void TestAdaptiveSpeedAcceptsGenerationBoost()
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

static void TestAdaptiveSpeedAcceptsEndToEndGain()
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

static void TestAdaptiveShortTaskProtection()
{
    var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
        0.05, 0.01, 1, 1, 20, 40,
        TaskDurationCategory.Short, AdaptivePreference.Balanced, 10));

    Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.ShortTaskProtected,
        "A short task switched outside Cost mode.");
}

static void TestAdaptiveInvalidPerformance()
{
    var result = AdaptiveSwitchDecisionEngine.Decide(new AdaptiveSwitchRequest(
        0.05, 0.02, 1, double.NaN, 20, 20,
        TaskDurationCategory.Medium, AdaptivePreference.Balanced, 10));

    Assert(!result.ShouldSwitch && result.Reason == AdaptiveDecisionReason.BalancedGuardRejected,
        "Invalid performance data accidentally satisfied the Balanced safeguards.");
}

static void TestAdaptiveInvalidOldPerformance()
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

static void TestWarningPresentationExcludesServerMessage()
{
    const string sensitiveWarning = "synthetic-sensitive-warning";
    var provider = new ProviderStatus
    {
        WarningReasons =
        [
            new ProviderWarningReason { Type = "latency_spike", Message = sensitiveWarning }
        ]
    };

    var state = ProviderStatusPresentation.DecorateRoutableState("可路由", provider);
    Assert(state == "可路由（警告）", "Warning state was not decorated safely.");
    Assert(!state.Contains(sensitiveWarning, StringComparison.Ordinal), "Warning message leaked into presentation state.");
}

static void TestWarningDecorationRequiresRoutableLatestState()
{
    var now = DateTimeOffset.UtcNow;
    var provider = Provider(1, 0.01, available: false, success: 1, now, warning: true);
    var state = ProviderStatusPresentation.ResolveRoutingState(
        provider,
        hasAccountData: true,
        isAuthorized: true,
        effectiveMultiplier: provider.PriceMultiplier,
        minimumSuccessRate6h: 0.9,
        now: now,
        maximumStatusAge: TimeSpan.FromMinutes(15));
    Assert(state == "当前异常", "An unavailable warning provider was shown as routable.");
}

static void TestRoutingPresentationPreservesAvailabilityThreshold()
{
    var now = DateTimeOffset.UtcNow;
    var provider = Provider(1, 0.01, available: true, success: 0.49, now, warning: true);
    var state = ProviderStatusPresentation.ResolveRoutingState(
        provider,
        hasAccountData: true,
        isAuthorized: true,
        effectiveMultiplier: provider.PriceMultiplier,
        minimumSuccessRate6h: 0.5,
        now: now,
        maximumStatusAge: TimeSpan.FromMinutes(15));
    Assert(state == "低于阈值", "Warning decoration bypassed the local availability threshold.");
}

static void TestRoutingPresentationRejectsInvalidEffectiveRate()
{
    var now = DateTimeOffset.UtcNow;
    var provider = Provider(1, 0.01, available: true, success: 1, now, warning: true);
    var state = ProviderStatusPresentation.ResolveRoutingState(
        provider,
        hasAccountData: true,
        isAuthorized: true,
        effectiveMultiplier: -0.1,
        minimumSuccessRate6h: 0.9,
        now: now,
        maximumStatusAge: TimeSpan.FromMinutes(15));
    Assert(state == "倍率无效", "An invalid effective rate was shown as routable.");
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
    Assert(settings.DurationCategory == TaskDurationCategory.Medium, "New installs must default to Medium duration.");
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
            DurationCategory = TaskDurationCategory.Long,
            AccountCacheSeconds = 90,
            Theme = WinFormsTheme.Dark
        }, null);

        var loaded = store.Load().Settings;
        Assert(loaded.RoutingMode == RoutingMode.Speed, "Routing mode did not roundtrip.");
        Assert(loaded.DurationCategory == TaskDurationCategory.Long, "Duration category did not roundtrip.");
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
}

static void TestCostModeProposesCheapest()
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

static void TestCostModeFallsBackWhenCheapestTtftIsUnknown()
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

    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "Cost mode did not fall back to the cheapest candidate with valid completion metrics.");
    Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveCostAccepted,
        "Cost fallback did not preserve the accepted adaptive reason.");
}

static void TestFrequentCallsOverrideEconomy()
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
    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
        "A frequent call interval did not allow the faster candidate.");
    Assert(result.Decision.EffectivePreference == AdaptivePreference.Speed,
        "Economy was not dynamically overridden with Speed.");
}

static void TestIdleCallsOverrideSpeed()
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

static void TestAdaptiveRejectionKeepsCurrentGroup()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [
            Provider(1, 0.05, true, 0.99, now, 1_000, outputTps: 20),
            Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 5)
        ],
        [Group(1), Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    var result = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 1 },
        Policy(RoutingMode.Balanced),
        new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Medium, 10),
        now,
        observedCurrentGroupId: 1);
    Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 1,
        "A rejected adaptive candidate replaced the current group.");
    Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveBalancedRejected,
        "The rejection did not expose its adaptive reason.");
}

static void TestAdaptiveAcceptanceUpdatesKeys()
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

static void TestAdaptiveTraversalFindsAcceptedCandidateBeyondWeightedWinner()
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
        Provider(3, 0.01, true, 0.99, now, 1_000, outputTps: 100),
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
        Policy(RoutingMode.Balanced),
        new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Medium, 10),
        now,
        observedCurrentGroupId: 1);

    Assert(result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 3,
        "Adaptive traversal did not select the accepted candidate beyond the weighted winner.");
    Assert(Math.Abs(result.Decision.PricePremiumPercent) < 1e-12,
        "Adaptive traversal reported the weighted winner's price premium instead of the selected candidate's premium.");
    Assert(result.Decision.Reason == RouteDecisionReason.AdaptiveBalancedAccepted,
        "Adaptive traversal did not preserve the accepted decision reason.");
}

static void TestAdaptiveRecoveryBypassesGuard()
{
    var now = DateTimeOffset.UtcNow;
    var evaluation = RoutingEngine.Evaluate(
        [Provider(2, 0.02, true, 0.99, now, 1_000, outputTps: 0)],
        [Group(2)],
        new Dictionary<long, double>(),
        Policy(RoutingMode.Balanced),
        now);
    var initial = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState(),
        Policy(RoutingMode.Balanced),
        new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Short, 10),
        now);
    var invalid = RouteDecisionEngine.Decide(
        evaluation,
        new RouteState { CurrentGroupId = 9 },
        Policy(RoutingMode.Balanced),
        new AdaptiveRoutingContext(RoutingMode.Balanced, TaskDurationCategory.Short, 10),
        now,
        observedCurrentGroupId: 9);

    Assert(initial.Decision.ShouldSwitch && initial.Decision.Reason == RouteDecisionReason.InitialRoute,
        "Initial routing was blocked by pairwise safeguards.");
    Assert(invalid.Decision.ShouldSwitch && invalid.Decision.Reason == RouteDecisionReason.CurrentRouteInvalid,
        "Invalid-route recovery was blocked by pairwise safeguards.");
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
            RouteDecisionReason.AdaptiveBalancedAccepted,
            1,
            2,
            false,
            [
                new RouteAuditCandidate(2, 0.02, 250, 0.4, true),
                new RouteAuditCandidate(3, double.NaN, double.PositiveInfinity, double.NegativeInfinity, false)
            ],
            [new RouteAuditKey(10, true, true, null)])
        {
            EffectivePreference = AdaptivePreference.Balanced,
            DurationCategory = TaskDurationCategory.Medium,
            CurrentIntervalSeconds = 10,
            AdaptiveReason = AdaptiveDecisionReason.AcceptedBalanced,
            PenaltyUsd = 0.03,
            NetSavingUsd = 0.04,
            OldCompletionSeconds = 4_000,
            NewCompletionSeconds = 3_900,
            DeltaSeconds = -100
        };
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
                Assert(document.RootElement.TryGetProperty("effectivePreference", out _),
                    "Audit JSON omitted the effective preference.");
                Assert(document.RootElement.TryGetProperty("durationCategory", out _),
                    "Audit JSON omitted the duration category.");
                Assert(document.RootElement.TryGetProperty("currentIntervalSeconds", out _),
                    "Audit JSON omitted the current interval.");
                Assert(document.RootElement.TryGetProperty("adaptiveReason", out _),
                    "Audit JSON omitted the adaptive reason.");
                Assert(document.RootElement.TryGetProperty("penaltyUsd", out _) &&
                    document.RootElement.TryGetProperty("netSavingUsd", out _) &&
                    document.RootElement.TryGetProperty("oldCompletionSeconds", out _) &&
                    document.RootElement.TryGetProperty("newCompletionSeconds", out _) &&
                    document.RootElement.TryGetProperty("deltaSeconds", out _),
                    "Audit JSON omitted adaptive numeric metrics.");
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
        Assert(exception.IsAuthenticationRequest, "Authentication endpoint context was discarded.");
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

static void TestAuthenticationStatusesHaveSafeDiagnostics()
{
    const string sensitiveText =
        "email=private@example.test password=secret Token=token-value Cookie=session-value User-Agent=private-agent raw-server-text";
    var cases = new (HttpStatusCode StatusCode, string ExpectedFragment)[]
    {
        (HttpStatusCode.Unauthorized, "邮箱或密码不正确"),
        (HttpStatusCode.Forbidden, "平台策略拒绝"),
        (HttpStatusCode.TooManyRequests, "1 分钟"),
        (HttpStatusCode.ServiceUnavailable, "暂时不可用")
    };
    var messages = new List<string>();

    foreach (var item in cases)
    {
        var exception = new AIHubApiException(
            sensitiveText,
            item.StatusCode,
            sensitiveText,
            isAuthenticationRequest: true);
        var message = SafeErrorPresentation.GetMessage(exception);
        messages.Add(message);

        Assert(message.Contains(item.ExpectedFragment, StringComparison.Ordinal),
            $"HTTP {(int)item.StatusCode} did not receive actionable authentication guidance.");
        Assert(!message.Contains(sensitiveText, StringComparison.Ordinal),
            $"HTTP {(int)item.StatusCode} exposed the raw authentication error.");
    }

    Assert(messages.Distinct(StringComparer.Ordinal).Count() == cases.Length,
        "Authentication status diagnostics were collapsed into the same message.");
}

static void TestBusinessAuthenticationStatusesKeepBusinessDiagnostics()
{
    const string sensitiveText = "email=private@example.test password=secret token-value";

    var unauthorized = SafeErrorPresentation.GetMessage(new AIHubApiException(
        sensitiveText,
        HttpStatusCode.Unauthorized,
        "401"));
    Assert(unauthorized.Contains("Token/session 已失效", StringComparison.Ordinal),
        "Business 401 did not explain that the saved session is invalid.");
    Assert(!unauthorized.Contains("邮箱或密码不正确", StringComparison.Ordinal),
        "Business 401 was incorrectly presented as a password failure.");

    var forbidden = SafeErrorPresentation.GetMessage(new AIHubApiException(
        sensitiveText,
        HttpStatusCode.Forbidden,
        "403"));
    Assert(forbidden == "当前账号没有执行该操作的权限。",
        "Business 403 did not preserve the permission diagnostic.");
}

static void TestMalformedAuthenticationResponsesRetainEndpointContext()
{
    var responseBodies = new[]
    {
        string.Empty,
        "{",
        "{\"code\":0,\"message\":\"ok\"}",
        "{\"code\":0,\"message\":\"ok\",\"data\":null}",
        "{\"code\":0,\"message\":\"ok\",\"data\":[]}"
    };

    foreach (var responseBody in responseBodies)
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(responseBody));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);

        try
        {
            client.LoginAsync(
                new LoginCredentials("user@example.test", "synthetic-password"),
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Malformed authentication response was accepted.");
        }
        catch (AIHubApiException exception)
        {
            Assert(exception.IsAuthenticationRequest,
                "Malformed authentication response lost the endpoint context.");
            Assert(SafeErrorPresentation.GetMessage(exception).StartsWith("认证", StringComparison.Ordinal),
                "Malformed authentication response did not use authentication guidance.");
        }
    }
}

static void TestUnknownErrorsDoNotExposeCredentialText()
{
    const string sensitiveText =
        "email=private@example.test password=secret Token=token-value Cookie=session-value User-Agent=private-agent";

    var message = SafeErrorPresentation.GetMessage(new Exception(sensitiveText));

    Assert(message == "操作失败，请重试。", "Unknown errors did not use the fixed safe fallback.");
    Assert(!message.Contains(sensitiveText, StringComparison.Ordinal), "Unknown error exposed credential text.");
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
    bool warning = false,
    double? outputTps = 20,
    DateTimeOffset? lastCallEndedAt = null)
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
        LastCallEndedAt = lastCallEndedAt,
        FirstTokenLatencyMs = latency,
        OutputTokensPerSecond = outputTps,
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
    public IReadOnlyList<ProviderStatus>? ProvidersOverride { get; init; }
    public IReadOnlyList<GroupInfo>? GroupsOverride { get; init; }

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default)
    {
        SummaryCalls++;
        if (ThrowNetwork) throw new HttpRequestException("synthetic network failure");
        if (FailFirstSummaryAuth && SummaryCalls == 1)
            throw new AIHubApiException("Authentication required.", HttpStatusCode.Unauthorized, "401");
        return Task.FromResult(new MonitorSummary
        {
            Apis = ProvidersOverride?.ToList() ?? [ProviderForStub(2, now)]
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
        return Task.FromResult(GroupsOverride ?? [GroupForStub(2)]);
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
        OutputTokensPerSecond = 20,
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
