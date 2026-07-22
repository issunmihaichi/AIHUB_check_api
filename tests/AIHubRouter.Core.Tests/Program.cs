using AIHubRouter.Core;
using AIHubRouter.Core.Tests;

var failures = 0;
foreach (var test in TestCatalog.All)
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
        TestFixtures.Assert(summary.Apis.Count > 0, "Public provider endpoint returned no entries.");
        Console.WriteLine($"PASS Public API smoke test ({summary.Apis.Count} entries)");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL Public API smoke test: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;
