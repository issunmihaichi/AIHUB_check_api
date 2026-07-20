using AIHubRouter.Core;
using System.Text;

var tests = new (string Name, Action Body)[]
{
    ("Bearer token normalization", TestBearerNormalization),
    ("Token extraction from cookie", TestCookieTokenExtraction),
    ("Lowest available authorized group", TestLowestAvailableGroup),
    ("User rate override", TestUserRateOverride),
    ("Availability threshold", TestAvailabilityThreshold),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Encrypted settings roundtrip", TestEncryptedSettingsRoundtrip)
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
            SmoothRendering = true
        };
        var credentials = new PersistentCredentials
        {
            BearerToken = secretToken,
            Cookie = "session=secret-cookie",
            UserAgent = "test-user-agent"
        };

        store.Save(settings, credentials);
        var encrypted = File.ReadAllBytes(Path.Combine(directory, "credentials.dat"));
        Assert(!Encoding.UTF8.GetString(encrypted).Contains(secretToken, StringComparison.Ordinal), "Credential file contains plaintext token.");

        var loaded = store.Load();
        Assert(loaded.Settings.PersistCredentials, "Persistence flag was not restored.");
        Assert(loaded.Settings.PollingIntervalSeconds == 120, "Polling interval was not restored.");
        Assert(loaded.Credentials?.BearerToken == secretToken, "Encrypted token did not roundtrip.");
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

static ProviderStatus Provider(long groupId, double rate, bool available, double success, DateTimeOffset checkedAt)
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
        FirstTokenLatencyMs = 1000,
        SuccessRates = new Dictionary<string, double> { ["6h"] = success }
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
