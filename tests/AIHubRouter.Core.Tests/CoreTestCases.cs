using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestCatalogRejectsDuplicateNames()
    {
        try
        {
            TestCatalog.Create(
                new TestCase("duplicate", static () => { }),
                new TestCase("duplicate", static () => { }));
            throw new InvalidOperationException("Duplicate test names were accepted.");
        }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message == "Duplicate test name: duplicate.",
                "Duplicate test names did not report the expected error.");
        }
    }

    internal static void TestCatalogSnapshotsInput()
    {
        var tests = new[] { new TestCase("original", static () => { }) };
        var catalog = TestCatalog.Create(tests);

        tests[0] = new TestCase("replacement", static () => { });

        Assert(catalog[0].Name == "original", "Test catalog retained the caller-owned test array.");
    }

    internal static void TestBearerNormalization()
    {
        Assert(CredentialParser.NormalizeBearerToken("Authorization: Bearer abc.def") == "abc.def", "Header was not normalized.");
        Assert(CredentialParser.NormalizeBearerToken("Bearer token") == "token", "Bearer prefix was not removed.");
    }

    internal static void TestCookieTokenExtraction()
    {
        var token = CredentialParser.TryExtractTokenFromCookie("theme=dark; auth_token=abc%2Edef; lang=zh");
        Assert(token == "abc.def", "auth_token cookie was not decoded.");
    }

    internal static void TestLowestAvailableGroup()
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

    internal static void TestUserRateOverride()
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

    internal static void TestAvailabilityThreshold()
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

    internal static void TestProviderWarningsDeserialize()
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

    internal static void TestNullProviderWarningsAreTolerated()
    {
        var provider = JsonSerializer.Deserialize<ProviderStatus>("""
            {"id":"provider-1","warningReasons":null}
            """)!;
        Assert(!provider.HasWarnings, "A null warning list was not treated as empty.");
    }

    internal static void TestProviderLastCallAliasesDeserialize()
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

    internal static void TestWarningProviderRemainsEligible()
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

    internal static void TestLatestUnavailableStateRemainsIneligible()
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

    internal static void TestWarningPresentationExcludesServerMessage()
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

    internal static void TestWarningDecorationRequiresRoutableLatestState()
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

    internal static void TestRoutingPresentationPreservesAvailabilityThreshold()
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
        Assert(!ProviderStatusPresentation.IsRoutable(
                provider,
                hasAccountData: true,
                isAuthorized: true,
                effectiveMultiplier: provider.PriceMultiplier,
                minimumSuccessRate6h: 0.5,
                now: now,
                maximumStatusAge: TimeSpan.FromMinutes(15)),
            "A provider below the local availability threshold was marked routable.");
    }

    internal static void TestRoutingPresentationRejectsInvalidEffectiveRate()
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

    internal static void TestStaleStatusRejection()
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

    internal static void TestRoutingPreferenceDefaults()
    {
        var settings = new PersistentAppSettings();
        Assert(settings.RoutingMode == RoutingMode.Economy, "New installs must preserve lowest-price routing.");
        Assert(settings.DurationCategory == TaskDurationCategory.Medium, "New installs must default to Medium duration.");
        Assert(settings.BalancedCountdownSeconds == 7_200, "Balanced countdown must preserve the Medium default.");
        Assert(settings.BalancedExpectedOutputTokens == 1_000,
            "Balanced expected output token budget must default independently from the countdown.");
        Assert(settings.BalancedDeadlineSoftSeconds == 5, "Balanced deadline soft tolerance must default to five seconds.");
        Assert(settings.AccountCacheSeconds == 300, "Account cache default changed.");
        Assert(settings.Theme == WinFormsTheme.System, "Theme must follow Windows by default.");
    }

    internal static void TestRoutingPreferenceRoundtrip()
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
                BalancedCountdownSeconds = 1_234.5,
                BalancedCountdownEndsAtUtc = DateTimeOffset.Parse("2026-07-21T10:00:00Z"),
                BalancedExpectedOutputTokens = 12_345,
                BalancedDeadlineSoftSeconds = 8.5,
                AccountCacheSeconds = 90,
                Theme = WinFormsTheme.Dark
            }, null);

            var loaded = store.Load().Settings;
            Assert(loaded.RoutingMode == RoutingMode.Speed, "Routing mode did not roundtrip.");
            Assert(loaded.DurationCategory == TaskDurationCategory.Long, "Duration category did not roundtrip.");
            Assert(Math.Abs(loaded.BalancedCountdownSeconds - 1_234.5) < 0.0001,
                "Balanced countdown duration did not roundtrip.");
            Assert(loaded.BalancedCountdownEndsAtUtc == DateTimeOffset.Parse("2026-07-21T10:00:00Z"),
                "Balanced countdown end timestamp did not roundtrip.");
            Assert(loaded.BalancedExpectedOutputTokens == 12_345,
                "Balanced expected output token budget did not roundtrip.");
            Assert(Math.Abs(loaded.BalancedDeadlineSoftSeconds - 8.5) < 0.0001,
                "Balanced deadline soft tolerance did not roundtrip.");
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

}
