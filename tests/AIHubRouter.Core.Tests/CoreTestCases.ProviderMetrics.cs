using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestRollingProviderMetricsDefaultToThirtyMinutes()
    {
        Assert(ProviderMetricsRollingWindow.DefaultWindow == TimeSpan.FromMinutes(30),
            "Provider metrics did not default to a thirty-minute rolling window.");
    }

    internal static void TestRollingProviderMetricsUseMedians()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();

        window.Observe(
            now.AddMinutes(-19),
            [Provider(1, 0.01, available: true, success: 0.6, checkedAt: now.AddMinutes(-19),
                latency: 100, outputTps: 10, lastCallEndedAt: now.AddMinutes(-18),
                enabled: true, lastCallAt: now.AddMinutes(-19))],
            new Dictionary<long, double> { [1] = 0.02 });
        window.Observe(
            now.AddMinutes(-10),
            [Provider(1, 0.03, available: false, success: 0.8, checkedAt: now.AddMinutes(-10),
                latency: 300, outputTps: 30, lastCallEndedAt: now.AddMinutes(-11),
                enabled: false, lastCallAt: now.AddMinutes(-12))],
            new Dictionary<long, double> { [1] = 0.04 });
        var snapshot = window.Observe(
            now,
            [Provider(1, 0.05, available: true, success: 1.0, checkedAt: now,
                latency: 500, outputTps: 50, lastCallEndedAt: now.AddMinutes(-2),
                enabled: true, lastCallAt: now.AddMinutes(-3))],
            new Dictionary<long, double> { [1] = 0.06 });

        var provider = snapshot.Providers.Single();
        Assert(provider.GroupId == 1, "Rolling metrics changed the provider group identity.");
        Assert(Math.Abs(provider.PriceMultiplier - 0.03) < 0.000001, "Public multiplier was not the median.");
        Assert(provider.Available, "Availability did not use the latest observation.");
        Assert(provider.Enabled, "Enabled state did not use the latest observation.");
        Assert(provider.CheckedAt == now, "CheckedAt did not use the latest observation.");
        Assert(provider.LastCallEndedAt == now.AddMinutes(-11), "LastCallEndedAt was not the timestamp median.");
        Assert(provider.LastCallAt == now.AddMinutes(-12), "LastCallAt was not the timestamp median.");
        Assert(Math.Abs((provider.FirstTokenLatencyMs ?? 0) - 300) < 0.000001, "TTFT was not the median.");
        Assert(Math.Abs((provider.OutputTokensPerSecond ?? 0) - 30) < 0.000001, "Output speed was not the median.");
        Assert(Math.Abs((provider.SuccessRate6h ?? 0) - 0.8) < 0.000001, "Six-hour success rate was not the median.");
        Assert(snapshot.UserGroupRates.TryGetValue(1, out var userRate) && Math.Abs(userRate - 0.04) < 0.000001,
            "User rate override was not the median.");
    }

    internal static void TestRollingProviderMetricsDiscardExpiredSamples()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();

        window.Observe(
            now.AddMinutes(-30).AddTicks(-1),
            [Provider(1, 0.99, available: true, success: 0.1,
                checkedAt: now.AddMinutes(-30).AddTicks(-1), latency: 900, outputTps: 90,
                lastCallEndedAt: now.AddMinutes(-30).AddTicks(-1),
                lastCallAt: now.AddMinutes(-30).AddTicks(-1))],
            new Dictionary<long, double> { [1] = 0.99 });
        window.Observe(
            now.AddMinutes(-30),
            [Provider(1, 0.01, available: true, success: 0.6,
                checkedAt: now.AddMinutes(-30), latency: 100, outputTps: 10,
                lastCallEndedAt: now.AddMinutes(-29), lastCallAt: now.AddMinutes(-30))],
            new Dictionary<long, double> { [1] = 0.02 });
        var snapshot = window.Observe(
            now,
            [Provider(1, 0.03, available: false, success: 1, checkedAt: now,
                latency: 300, outputTps: 30,
                lastCallEndedAt: now.AddMinutes(-1), lastCallAt: now)],
            new Dictionary<long, double> { [1] = 0.04 });

        var provider = snapshot.Providers.Single();
        Assert(Math.Abs(provider.PriceMultiplier - 0.02) < 0.000001,
            "The exact cutoff sample was not retained or the older sample affected the public multiplier median.");
        Assert(Math.Abs((provider.FirstTokenLatencyMs ?? 0) - 200) < 0.000001,
            "The thirty-minute TTFT median did not retain the exact cutoff sample.");
        Assert(Math.Abs((provider.OutputTokensPerSecond ?? 0) - 20) < 0.000001,
            "The thirty-minute output-speed median did not retain the exact cutoff sample.");
        Assert(provider.LastCallEndedAt == now.AddMinutes(-15),
            "The thirty-minute last-call-ended timestamp was not the retained-sample median.");
        Assert(provider.LastCallAt == now.AddMinutes(-15),
            "The thirty-minute last-call timestamp was not the retained-sample median.");
        Assert(Math.Abs((provider.SuccessRate6h ?? 0) - 0.8) < 0.000001,
            "The thirty-minute success-rate median did not retain the exact cutoff sample.");
        Assert(snapshot.UserGroupRates.TryGetValue(1, out var userRate) && Math.Abs(userRate - 0.03) < 0.000001,
            "The exact cutoff user rate was not retained or an older rate affected its median.");
    }

    internal static void TestRollingProviderMetricsUseLatestCurrentState()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();

        window.Observe(
            now.AddMinutes(-20),
            [Provider(1, 0.01, available: true, success: 0.6,
                checkedAt: now.AddMinutes(-20), latency: 100, outputTps: 10, enabled: true)],
            new Dictionary<long, double>());
        var unavailable = window.Observe(
            now.AddMinutes(-10),
            [Provider(1, 0.03, available: false, success: 0.8,
                checkedAt: now.AddMinutes(-10), latency: 300, outputTps: 30, enabled: false)],
            new Dictionary<long, double>());

        var unavailableProvider = unavailable.Providers.Single();
        Assert(!unavailableProvider.Available && !unavailableProvider.Enabled,
            "Historical true states overrode the latest unavailable or disabled state.");
        Assert(unavailableProvider.CheckedAt == now.AddMinutes(-10),
            "Historical timestamps overrode the latest CheckedAt value.");
        Assert(Math.Abs(unavailableProvider.PriceMultiplier - 0.02) < 0.000001,
            "Using latest state changed numeric median aggregation.");

        var restored = window.Observe(
            now,
            [Provider(1, 0.05, available: true, success: 1,
                checkedAt: now, latency: 500, outputTps: 50, enabled: true)],
            new Dictionary<long, double>());

        var restoredProvider = restored.Providers.Single();
        Assert(restoredProvider.Available && restoredProvider.Enabled,
            "A later healthy observation did not restore availability and enabled state.");
        Assert(restoredProvider.CheckedAt == now,
            "A later healthy observation did not restore the latest CheckedAt value.");
        Assert(Math.Abs(restoredProvider.PriceMultiplier - 0.03) < 0.000001 &&
            Math.Abs((restoredProvider.FirstTokenLatencyMs ?? 0) - 300) < 0.000001 &&
            Math.Abs((restoredProvider.OutputTokensPerSecond ?? 0) - 30) < 0.000001 &&
            Math.Abs((restoredProvider.SuccessRate6h ?? 0) - 0.8) < 0.000001,
            "Restoring latest state changed numeric median aggregation.");
    }

    internal static void TestRollingProviderMetricsClearHistory()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();

        window.Observe(
            now.AddMinutes(-1),
            [Provider(1, 0.01, available: true, success: 1, checkedAt: now.AddMinutes(-1))],
            new Dictionary<long, double> { [1] = 0.10 });
        window.Clear();
        var snapshot = window.Observe(
            now,
            [Provider(1, 0.03, available: true, success: 1, checkedAt: now)],
            new Dictionary<long, double> { [1] = 0.02 });

        Assert(Math.Abs(snapshot.Providers.Single().PriceMultiplier - 0.03) < 0.000001,
            "A cleared window retained a prior account's public multiplier.");
        Assert(snapshot.UserGroupRates.TryGetValue(1, out var rate) && Math.Abs(rate - 0.02) < 0.000001,
            "A cleared window retained a prior account's user-rate override.");
    }

    internal static void TestRollingProviderMetricsAreStableForDuplicateGroups()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();
        var providerA = Provider(1, 0.01, available: true, success: 1, checkedAt: now.AddMinutes(-1),
            latency: 100, outputTps: 10, id: "provider-a");
        var providerZ = Provider(1, 0.03, available: true, success: 1, checkedAt: now.AddMinutes(-1),
            latency: 300, outputTps: 30, id: "provider-z");

        window.Observe(now.AddMinutes(-1), [providerZ, providerA], new Dictionary<long, double>());
        var snapshot = window.Observe(now, [providerA, providerZ], new Dictionary<long, double>());

        var provider = snapshot.Providers.Single();
        Assert(provider.Id == "provider-a", "Duplicate groups depended on API array order.");
        Assert(Math.Abs(provider.PriceMultiplier - 0.02) < 0.000001,
            "Duplicate-group public multiplier was not aggregated by median.");
        Assert(Math.Abs((provider.FirstTokenLatencyMs ?? 0) - 200) < 0.000001,
            "Duplicate-group TTFT was not aggregated by median.");
    }

    internal static void TestRollingProviderMetricsUseLatestDuplicateState()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();

        var unavailable = window.Observe(
            now,
            [
                Provider(1, 0.01, available: true, success: 1,
                    checkedAt: now.AddMinutes(-1), enabled: true, id: "provider-a"),
                Provider(1, 0.03, available: false, success: 1,
                    checkedAt: now, enabled: false, id: "provider-z")
            ],
            new Dictionary<long, double>());

        var unavailableProvider = unavailable.Providers.Single();
        Assert(!unavailableProvider.Available && !unavailableProvider.Enabled &&
            unavailableProvider.CheckedAt == now,
            "An older duplicate row masked the newest unavailable or disabled state.");

        var restored = window.Observe(
            now.AddMinutes(1),
            [
                Provider(1, 0.05, available: false, success: 1,
                    checkedAt: now, enabled: false, id: "provider-a"),
                Provider(1, 0.07, available: true, success: 1,
                    checkedAt: now.AddMinutes(1), enabled: true, id: "provider-z")
            ],
            new Dictionary<long, double>());

        var restoredProvider = restored.Providers.Single();
        Assert(restoredProvider.Available && restoredProvider.Enabled &&
            restoredProvider.CheckedAt == now.AddMinutes(1),
            "The newest healthy duplicate row did not restore current state.");
    }

    internal static void TestRollingProviderMetricsExposeConservativePerformance()
    {
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(now.AddMinutes(-2),
            [Provider(1, 0.01, true, 1, now.AddMinutes(-2), latency: 100, outputTps: 10)],
            new Dictionary<long, double>());
        window.Observe(now.AddMinutes(-1),
            [Provider(1, 0.01, true, 1, now.AddMinutes(-1), latency: 300, outputTps: 30)],
            new Dictionary<long, double>());
        var snapshot = window.Observe(now,
            [Provider(1, 0.01, true, 1, now, latency: 500, outputTps: 50)],
            new Dictionary<long, double>());

        var provider = snapshot.Providers.Single();
        Assert(provider.PerformanceSampleCount == 3,
            "Rolling metrics did not expose the performance sample count.");
        Assert(provider.FirstTokenLatencyP90Ms == 500,
            "Rolling metrics did not use the conservative P90 first-token latency.");
        Assert(provider.OutputTokensPerSecondP25 == 10,
            "Rolling metrics did not use the conservative P25 output speed.");
    }

}
