using AIHubRouter.Core;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestExactGroupBlocklistExcludesRouteCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [
                Provider(1, 0.01, true, 0.99, now),
                Provider(2, 0.02, true, 0.99, now)
            ],
            [Group(1), Group(2)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy) with
            {
                Blocklist = new ProviderBlocklist([1], [])
            },
            now);

        Assert(evaluation.EligibleCandidates.Count == 1, "Blocked group remained an eligible route candidate.");
        Assert(evaluation.Recommended?.Group.Id == 2, "Blocked group was still recommended.");
    }

    internal static void TestBlocklistPatternsMatchNodeFieldsIgnoringCase()
    {
        var byId = new ProviderBlocklist([], ["PROVIDER-7"]);
        var byPlan = new ProviderBlocklist([], ["pLaN 8"]);
        var byPlatform = new ProviderBlocklist([], ["OPENAI"]);

        Assert(byId.IsBlocked(Provider(7, 0.01, true, 0.99, DateTimeOffset.UtcNow), Group(7)),
            "Provider ID matching was not case-insensitive.");
        Assert(byPlan.IsBlocked(Provider(8, 0.01, true, 0.99, DateTimeOffset.UtcNow), Group(8)),
            "Plan matching was not case-insensitive.");
        Assert(byPlatform.IsBlocked(Provider(9, 0.01, true, 0.99, DateTimeOffset.UtcNow), Group(9)),
            "Platform matching was not case-insensitive.");
    }

    internal static void TestUnmatchedBlocklistRuleKeepsCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = RoutingEngine.Evaluate(
            [Provider(1, 0.01, true, 0.99, now)],
            [Group(1)],
            new Dictionary<long, double>(),
            Policy(RoutingMode.Economy) with
            {
                Blocklist = new ProviderBlocklist([], ["does-not-match"])
            },
            now);

        Assert(evaluation.EligibleCandidates.Count == 1, "An unmatched blacklist rule removed a route candidate.");
    }

    internal static void TestBlocklistSettingsRoundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(directory);
            store.Save(new PersistentAppSettings
            {
                BlockedGroupIds = [7, 42],
                BlockedNodePatterns = ["slow", "trial"]
            }, null);

            var settings = store.Load().Settings;
            Assert(settings.BlockedGroupIds.SequenceEqual(new long[] { 7, 42 }), "Blocked group IDs did not persist.");
            Assert(settings.BlockedNodePatterns.SequenceEqual(new[] { "slow", "trial" }), "Blocked node patterns did not persist.");
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
