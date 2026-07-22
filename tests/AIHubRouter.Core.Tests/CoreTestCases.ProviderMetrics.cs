using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
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
        Assert(provider.Available, "Availability did not use the boolean median.");
        Assert(provider.Enabled, "Enabled state did not use the boolean median.");
        Assert(provider.CheckedAt == now.AddMinutes(-10), "CheckedAt was not the timestamp median.");
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
            now.AddMinutes(-21),
            [Provider(1, 0.01, available: true, success: 1, checkedAt: now.AddMinutes(-21), latency: 100, outputTps: 10)],
            new Dictionary<long, double> { [1] = 0.02 });
        var snapshot = window.Observe(
            now,
            [Provider(1, 0.03, available: false, success: 0.5, checkedAt: now, latency: 300, outputTps: 30)],
            new Dictionary<long, double> { [1] = 0.04 });

        var provider = snapshot.Providers.Single();
        Assert(Math.Abs(provider.PriceMultiplier - 0.03) < 0.000001, "An expired public multiplier affected the median.");
        Assert(!provider.Available, "An expired availability sample affected the median.");
        Assert(snapshot.UserGroupRates.TryGetValue(1, out var userRate) && Math.Abs(userRate - 0.04) < 0.000001,
            "An expired user-rate sample affected the median.");
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

}
