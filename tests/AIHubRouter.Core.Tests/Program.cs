using AIHubRouter.Core;
using System.Net;
using System.Text;

var tests = new (string Name, Action Body)[]
{
    ("Bearer token normalization", TestBearerNormalization),
    ("Token extraction from cookie", TestCookieTokenExtraction),
    ("Lowest available authorized group", TestLowestAvailableGroup),
    ("User rate override", TestUserRateOverride),
    ("Availability threshold", TestAvailabilityThreshold),
    ("Stale status rejection", TestStaleStatusRejection),
    ("Encrypted settings roundtrip", TestEncryptedSettingsRoundtrip),
    ("Usable access token is reused", TestUsableAccessTokenIsReused),
    ("Expired access token refreshes first", TestExpiredAccessTokenRefreshesFirst),
    ("Rejected refresh falls back to login", TestRejectedRefreshFallsBackToLogin),
    ("Login endpoint maps session", TestLoginEndpointMapsSession),
    ("Refresh endpoint maps rotated session", TestRefreshEndpointMapsRotatedSession),
    ("Interactive login requirement is rejected", TestInteractiveLoginRequirementIsRejected),
    ("Empty key selection roundtrips", TestEmptyKeySelectionRoundtrips)
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

static HttpResponseMessage JsonResponse(string json)
{
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
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

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}
