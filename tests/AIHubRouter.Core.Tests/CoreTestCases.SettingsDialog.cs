using AIHubRouter.Core;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestRoutingUiSettingsNormalize()
    {
        var normalized = new RoutingUiSettings
        {
            MinimumSuccessPercent = -4,
            PollingIntervalSeconds = 9_999,
            AccountCacheSeconds = 1,
            AutoRoute = true,
            DurationCategory = TaskDurationCategory.Long,
            BalancedSoftDeadlineSeconds = 999,
            BalancedExpectedOutputTokens = -20,
            ActiveProbeEnabled = true,
            ActiveProbeKeyId = 0,
            ActiveProbeModel = "  gpt-test  ",
            Theme = WinFormsTheme.Dark,
            SmoothRendering = false,
            BlockedGroupIds = [4, -1, 4, 2],
            BlockedNodePatterns = ["  slow  ", "", "SLOW", "  " ]
        }.Normalize();

        Assert(normalized.MinimumSuccessPercent == 0, "Minimum success rate was not clamped.");
        Assert(normalized.PollingIntervalSeconds == 3_600, "Polling interval was not clamped.");
        Assert(normalized.AccountCacheSeconds == 30, "Account cache duration was not clamped.");
        Assert(normalized.AutoRoute, "Automatic routing flag was not preserved.");
        Assert(normalized.DurationCategory == TaskDurationCategory.Long, "Task size was not preserved.");
        Assert(normalized.BalancedSoftDeadlineSeconds == 300, "Soft deadline was not clamped.");
        Assert(normalized.BalancedExpectedOutputTokens == 0, "Expected output was not clamped.");
        Assert(normalized.ActiveProbeKeyId is null, "Invalid probe Key ID was not cleared.");
        Assert(normalized.ActiveProbeModel == "gpt-test", "Probe model was not trimmed.");
        Assert(normalized.Theme == WinFormsTheme.Dark && !normalized.SmoothRendering,
            "Non-numeric settings were not preserved.");
        Assert(normalized.BlockedGroupIds.SequenceEqual([2L, 4L]), "Blocked group IDs were not normalized.");
        Assert(normalized.BlockedNodePatterns.SequenceEqual(["slow"]),
            "Blocked node patterns were not trimmed and case-insensitive duplicates were collapsed.");

        var opposite = new RoutingUiSettings
        {
            MinimumSuccessPercent = 101,
            PollingIntervalSeconds = 0,
            AccountCacheSeconds = int.MaxValue,
            DurationCategory = (TaskDurationCategory)99,
            BalancedSoftDeadlineSeconds = double.NegativeInfinity,
            BalancedExpectedOutputTokens = double.PositiveInfinity,
            ActiveProbeModel = null!,
            BlockedGroupIds = default,
            BlockedNodePatterns = default
        }.Normalize();

        Assert(opposite.MinimumSuccessPercent == 100, "Maximum success rate was not clamped.");
        Assert(opposite.PollingIntervalSeconds == 30, "Minimum polling interval was not clamped.");
        Assert(opposite.AccountCacheSeconds == 3_600, "Maximum account cache duration was not clamped.");
        Assert(opposite.DurationCategory == TaskDurationCategory.Medium,
            "An unknown task size did not use the Medium default.");
        Assert(opposite.BalancedSoftDeadlineSeconds == 5, "Infinite soft deadline did not use the default.");
        Assert(opposite.BalancedExpectedOutputTokens == 1_000, "Infinite expected output did not use the default.");
        Assert(opposite.ActiveProbeModel == string.Empty, "Null probe model was not normalized.");
        Assert(opposite.BlockedGroupIds.IsEmpty && opposite.BlockedNodePatterns.IsEmpty,
            "Default immutable collections were not normalized to empty collections.");

        var finiteLowerBounds = new RoutingUiSettings
        {
            BalancedSoftDeadlineSeconds = -1
        }.Normalize();
        var finiteUpperBounds = new RoutingUiSettings
        {
            BalancedExpectedOutputTokens = 10_000_001
        }.Normalize();

        Assert(finiteLowerBounds.BalancedSoftDeadlineSeconds == 0,
            "Finite soft deadline below the supported range was not clamped.");
        Assert(finiteUpperBounds.BalancedExpectedOutputTokens == 10_000_000,
            "Finite expected output above the supported range was not clamped.");
    }

    internal static void TestRoutingUiSettingsEquivalentToNormalizedSnapshot()
    {
        var first = new RoutingUiSettings
        {
            MinimumSuccessPercent = 50,
            PollingIntervalSeconds = 60,
            AccountCacheSeconds = 300,
            DurationCategory = TaskDurationCategory.Long,
            BalancedSoftDeadlineSeconds = 5,
            BalancedExpectedOutputTokens = 1_000,
            BlockedGroupIds = [7, 3],
            BlockedNodePatterns = ["beta", "alpha"]
        };
        var equivalent = new RoutingUiSettings
        {
            MinimumSuccessPercent = 50,
            PollingIntervalSeconds = 60,
            AccountCacheSeconds = 300,
            DurationCategory = TaskDurationCategory.Long,
            BalancedSoftDeadlineSeconds = 5,
            BalancedExpectedOutputTokens = 1_000,
            BlockedGroupIds = [3, 7, 7],
            BlockedNodePatterns = [" ALPHA ", "BETA", "alpha"]
        };
        var changed = first with { AccountCacheSeconds = 330 };

        Assert(first.IsEquivalentTo(equivalent), "Equivalent normalized drafts were treated as changed.");
        Assert(first == equivalent, "Native record equality did not use normalized structural semantics.");
        Assert(first.GetHashCode() == equivalent.GetHashCode(),
            "Equivalent normalized records produced different hash codes.");
        Assert(!first.IsEquivalentTo(changed), "A changed setting was treated as equivalent.");

        var normalized = first.Normalize();
        var added = normalized with { BlockedGroupIds = normalized.BlockedGroupIds.Add(11) };

        Assert(normalized.BlockedGroupIds.SequenceEqual([3L, 7L]),
            "Creating a changed snapshot mutated the normalized original.");
        Assert(added.BlockedGroupIds.SequenceEqual([3L, 7L, 11L]),
            "Immutable collection Add did not create the requested changed snapshot.");
        Assert(normalized != added, "A changed immutable snapshot compared equal to its original.");
    }

    internal static void TestRoutingPersistencePolicySuppressesBatchWrites()
    {
        Assert(!RoutingPersistencePolicy.ShouldPersistCredentials(
                persistCredentials: true,
                applyingRoutingSettings: true,
                suppressRoutingPersistence: false),
            "Credential persistence was allowed while applying routing settings.");
        Assert(!RoutingPersistencePolicy.ShouldPersistCredentials(
                persistCredentials: true,
                applyingRoutingSettings: false,
                suppressRoutingPersistence: true),
            "Credential persistence was allowed while the follow-up routing cycle was suppressed.");
        Assert(RoutingPersistencePolicy.ShouldPersistCredentials(
                persistCredentials: true,
                applyingRoutingSettings: false,
                suppressRoutingPersistence: false),
            "Normal credential persistence was incorrectly suppressed.");
        Assert(!RoutingPersistencePolicy.ShouldPersistCredentials(
                persistCredentials: false,
                applyingRoutingSettings: false,
                suppressRoutingPersistence: false),
            "Credential persistence was allowed when the user disabled persistence.");
    }
}
