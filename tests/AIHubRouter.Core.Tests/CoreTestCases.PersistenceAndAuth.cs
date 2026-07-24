using AIHubRouter.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestBusinessAuthenticationRetry()
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

    internal static void TestAuditLogWritesValidJsonAndRotates()
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
                    new RouteAuditCandidate(2, 0.02, 250, 0.4, true)
                    {
                        LatencyP90Ms = 300,
                        OutputRateP25 = 20,
                        PerformanceSampleCount = 20
                    },
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
                DeltaSeconds = -100,
                SwitchClass = RouteSwitchClass.Policy,
                CompletedPolicyEvaluationsSinceLastSwitch = 6,
                PendingPolicyTargetGroupId = 2,
                PendingPolicyTargetObservations = 2
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
                    Assert(document.RootElement.TryGetProperty("switchClass", out _) &&
                        document.RootElement.TryGetProperty("completedPolicyEvaluationsSinceLastSwitch", out _) &&
                        document.RootElement.TryGetProperty("pendingPolicyTargetGroupId", out _) &&
                        document.RootElement.TryGetProperty("pendingPolicyTargetObservations", out _),
                        "Audit JSON omitted policy-switch classification or hysteresis state.");
                    var candidate = document.RootElement.GetProperty("candidates")[0];
                    Assert(candidate.TryGetProperty("latencyP90Ms", out _) &&
                        candidate.TryGetProperty("outputRateP25", out _) &&
                        candidate.TryGetProperty("performanceSampleCount", out _),
                        "Audit JSON omitted conservative candidate performance metrics.");
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

    internal static void TestPublishScriptChecksNativeExitCodes()
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
        Assert(script.Contains(
                @"tests\AIHubRouter.WinForms.Tests\AIHubRouter.WinForms.Tests.csproj",
                StringComparison.Ordinal),
            "Publish script does not run the WinForms regression tests.");
    }

    internal static void TestEncryptedSettingsRoundtrip()
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

    internal static void TestUsableAccessTokenIsReused()
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

    internal static void TestExpiredAccessTokenRefreshesFirst()
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

    internal static void TestRejectedRefreshFallsBackToLogin()
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

    internal static void TestRefreshApiCodeFallsBackToLogin()
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

    internal static void TestHttp200FailureStatusOverridesSuccessCode()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"code":0,"status":"invalid_token","data":null}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);

        try
        {
            client.RefreshSessionAsync("refresh-rejected", CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Conflicting HTTP 200 authentication failure was accepted.");
        }
        catch (AIHubApiException exception)
        {
            Assert(exception.ApiCode == "invalid_token",
                "Failing status did not override the successful API code.");
            Assert(exception.IsAuthenticationFailure,
                "Failing authentication status was not classified for session recovery.");
        }
    }

    internal static void TestHttp200FailureStatusFallsBackToLogin()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"code":0,"status":"invalid_token","data":null}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);
        var loginCalls = 0;
        var coordinator = new SessionCoordinator(
            client.RefreshSessionAsync,
            (credentials, cancellationToken) =>
            {
                loginCalls++;
                return Task.FromResult(new AuthSession(
                    "access-login",
                    "refresh-login",
                    DateTimeOffset.UtcNow.AddHours(1)));
            },
            (session, cancellationToken) => Task.CompletedTask);

        var session = coordinator.GetSessionAsync(
            new AuthSession("access-expired", "refresh-rejected", DateTimeOffset.MinValue),
            new LoginCredentials("user@example.test", "password"),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert(loginCalls == 1, "Failing HTTP 200 status did not trigger login fallback.");
        Assert(session.AccessToken == "access-login", "Login fallback session was not returned.");
    }

    internal static void TestHttp200SpecificFailureCodeFallsBackToLogin()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"code":"invalid_token","status":"error","error":{"status":"error"},"data":null}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);
        var loginCalls = 0;
        var coordinator = new SessionCoordinator(
            client.RefreshSessionAsync,
            (credentials, cancellationToken) =>
            {
                loginCalls++;
                return Task.FromResult(new AuthSession(
                    "access-login",
                    "refresh-login",
                    DateTimeOffset.UtcNow.AddHours(1)));
            },
            (session, cancellationToken) => Task.CompletedTask);

        var session = coordinator.GetSessionAsync(
            new AuthSession("access-expired", "refresh-rejected", DateTimeOffset.MinValue),
            new LoginCredentials("user@example.test", "password"),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert(loginCalls == 1, "Specific failure code did not trigger login fallback.");
        Assert(session.AccessToken == "access-login", "Login fallback session was not returned.");
    }

    internal static void TestAuthenticationApiCodeIsClassified()
    {
        var exception = new AIHubApiException("Synthetic auth failure.", HttpStatusCode.OK, "401");
        Assert(exception.IsAuthenticationFailure, "API code 401 was not classified as an authentication failure.");
    }

    internal static void TestRefreshNetworkFailureDoesNotLogIn()
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

    internal static void TestLoginEndpointMapsSession()
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

    internal static void TestRefreshEndpointMapsRotatedSession()
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

    internal static void TestRefreshKeepsTokenWhenServerOmitsRotation()
    {
        var handler = new StubHttpMessageHandler(request => JsonResponse("""
            {"code":0,"message":"ok","data":{"access_token":"access-new","expires_in":1800,"token_type":"Bearer"}}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);

        var session = client.RefreshSessionAsync("refresh-current", CancellationToken.None).GetAwaiter().GetResult();

        Assert(session.RefreshToken == "refresh-current", "Refresh discarded the existing token when no rotation was returned.");
    }

    internal static void TestHttp200SuccessEnvelopesDeserialize()
    {
        var cases = new (string Name, string Json, string ExpectedProviderId)[]
        {
            ("numeric zero data", """{"code":0,"data":{"apis":[{"id":"zero-data"}]}}""", "zero-data"),
            ("2xx result", """{"code":204,"result":{"apis":[{"id":"two-hundred-result"}]}}""", "two-hundred-result"),
            ("OK payload", """{"code":"OK","payload":{"apis":[{"id":"ok-payload"}]}}""", "ok-payload"),
            ("SUCCESS data", """{"code":"SUCCESS","data":{"apis":[{"id":"success-data"}]}}""", "success-data"),
            ("boolean success result", """{"success":true,"result":{"apis":[{"id":"boolean-result"}]}}""", "boolean-result"),
            ("status success payload", """{"status":"success","payload":{"apis":[{"id":"status-payload"}]}}""", "status-payload")
        };

        foreach (var item in cases)
        {
            var handler = new StubHttpMessageHandler(_ => JsonResponse(item.Json));
            using var client = new AIHubClient("https://example.test", messageHandler: handler);

            var summary = client.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(summary.Apis.Count == 1 && summary.Apis[0].Id == item.ExpectedProviderId,
                $"HTTP 200 {item.Name} envelope was not unwrapped.");
        }
    }

    internal static void TestHttp200DirectBusinessResponsesDeserialize()
    {
        var monitorHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"apis":[{"id":"direct-monitor"}]}
            """));
        using var monitorClient = new AIHubClient("https://example.test", messageHandler: monitorHandler);
        var summary = monitorClient.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(summary.Apis.Count == 1 && summary.Apis[0].Id == "direct-monitor",
            "Direct MonitorSummary response was not deserialized.");

        var groupHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            [{"id":7,"name":"direct-group","status":"active"}]
            """));
        using var groupClient = new AIHubClient("https://example.test", messageHandler: groupHandler);
        var groups = groupClient.GetAvailableGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(groups.Count == 1 && groups[0].Status == "active",
            "Direct GroupInfo status was mistaken for an API response envelope.");

        var keyHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"id":18,"name":"direct-key","group_id":7,"status":"active","group":{"id":7,"name":"direct-group","status":"active"}}
            """));
        using var keyClient = new AIHubClient("https://example.test", messageHandler: keyHandler);
        var key = keyClient.UpdateKeyGroupAsync(18, 7, CancellationToken.None).GetAwaiter().GetResult();
        Assert(key.Status == "active" && key.Group?.Status == "active",
            "Direct business status was mistaken for an API response envelope.");

        var failureLikeStatusHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"id":19,"name":"direct-failure-like-status","group_id":7,"status":"UPSTREAM_FAILED"}
            """));
        using var failureLikeStatusClient = new AIHubClient(
            "https://example.test",
            messageHandler: failureLikeStatusHandler);
        var failureLikeStatusKey = failureLikeStatusClient
            .UpdateKeyGroupAsync(19, 7, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(failureLikeStatusKey.Status == "UPSTREAM_FAILED",
            "A lone direct business status was treated as envelope metadata.");

        var nullableErrorHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"id":20,"name":"direct-nullable-error","group_id":7,"status":"active","error":null}
            """));
        using var nullableErrorClient = new AIHubClient("https://example.test", messageHandler: nullableErrorHandler);
        var nullableErrorKey = nullableErrorClient
            .UpdateKeyGroupAsync(20, 7, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(nullableErrorKey.Status == "active",
            "A null direct error field was treated as an API response envelope.");
    }

    internal static void TestHttp200DirectJsonElementValidationResponseRemainsUsable()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"id":"account-1","status":"active"}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);

        var response = client.ValidateLoginAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert(response.ValueKind == JsonValueKind.Object &&
               response.GetProperty("status").GetString() == "active",
            "Direct JsonElement validation response was not preserved after parsing.");
    }

    internal static void TestHttp200EnvelopeRejectsExplicitBusinessErrors()
    {
        const string sensitiveText = "synthetic-api-key=do-not-expose";
        var cases = new (string Name, string Json, string? ExpectedCode)[]
        {
            ("success false", """{"success":false,"data":{"apis":[]}}""", null),
            ("nonempty error", """{"code":0,"error":"synthetic-api-key=do-not-expose","data":{"apis":[]}}""", "0"),
            ("failure code", """{"code":"UPSTREAM_FAILED","data":{"apis":[]}}""", "UPSTREAM_FAILED"),
            ("failure status", """{"status":"UPSTREAM_FAILED","data":{"apis":[]}}""", "UPSTREAM_FAILED"),
            ("unrecognized failure status", """{"status":"NOT_OK","data":{"apis":[]}}""", "NOT_OK"),
            ("conflicting success", """{"code":0,"success":false,"data":{"apis":[]}}""", "0")
        };

        foreach (var item in cases)
        {
            var handler = new StubHttpMessageHandler(_ => JsonResponse(item.Json));
            using var client = new AIHubClient("https://example.test", messageHandler: handler);

            try
            {
                client.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException($"HTTP 200 {item.Name} response was accepted.");
            }
            catch (AIHubApiException exception)
            {
                Assert(exception.ApiCode == item.ExpectedCode,
                    $"HTTP 200 {item.Name} response did not retain the expected API code.");
                Assert(!exception.Message.Contains(sensitiveText, StringComparison.Ordinal),
                    $"HTTP 200 {item.Name} response leaked server content.");
            }
        }
    }

    internal static void TestHttp200EnvelopeUsesNestedErrorCode()
    {
        const string sensitiveText = "temporary-token=do-not-expose";
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"code":0,"success":false,"error":{"code":"UPSTREAM_FAILED","message":"temporary-token=do-not-expose"},"data":{"apis":[]}}
            """));
        using var client = new AIHubClient("https://example.test", messageHandler: handler);

        try
        {
            client.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Nested HTTP 200 error was accepted.");
        }
        catch (AIHubApiException exception)
        {
            Assert(exception.ApiCode == "UPSTREAM_FAILED", "Nested error code was not retained.");
            Assert(!exception.Message.Contains(sensitiveText, StringComparison.Ordinal),
                "Nested HTTP 200 error leaked server content.");
        }
    }

    internal static void TestHttp200EnvelopeRejectsMalformedOrMissingPayload()
    {
        var responseBodies = new[]
        {
            string.Empty,
            "{",
            """{"success":true}""",
            """{"code":200,"data":null}""",
            """{"status":"success","payload":null}"""
        };

        foreach (var responseBody in responseBodies)
        {
            var handler = new StubHttpMessageHandler(_ => JsonResponse(responseBody));
            using var client = new AIHubClient("https://example.test", messageHandler: handler);

            try
            {
                client.GetProviderSummaryAsync(CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("Malformed HTTP 200 response was accepted.");
            }
            catch (AIHubApiException)
            {
            }
        }
    }

    internal static void TestHttp200EnvelopeSafePresentationHidesSuccessfulStatus()
    {
        const string sensitiveText = "synthetic-cookie=do-not-expose";
        var exception = new AIHubApiException(sensitiveText, HttpStatusCode.OK, "UPSTREAM_FAILED");

        var message = SafeErrorPresentation.GetMessage(exception);

        Assert(!message.Contains("HTTP 200", StringComparison.Ordinal),
            "HTTP 200 business failure was presented as an HTTP failure.");
        Assert(!message.Contains(sensitiveText, StringComparison.Ordinal),
            "HTTP 200 business failure exposed sensitive server content.");
    }

    internal static void TestAuthenticationErrorHidesServerMessage()
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

    internal static void TestBusinessErrorHidesServerMessage()
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

    internal static void TestAuthenticationStatusesHaveSafeDiagnostics()
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

    internal static void TestBusinessAuthenticationStatusesKeepBusinessDiagnostics()
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

    internal static void TestMalformedAuthenticationResponsesRetainEndpointContext()
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

    internal static void TestUnknownErrorsDoNotExposeCredentialText()
    {
        const string sensitiveText =
            "email=private@example.test password=secret Token=token-value Cookie=session-value User-Agent=private-agent";

        var message = SafeErrorPresentation.GetMessage(new Exception(sensitiveText));

        Assert(message == "操作失败，请重试。", "Unknown errors did not use the fixed safe fallback.");
        Assert(!message.Contains(sensitiveText, StringComparison.Ordinal), "Unknown error exposed credential text.");
    }

    internal static void TestInteractiveLoginRequirementIsRejected()
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

    internal static void TestEmptyKeySelectionRoundtrips()
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

    internal static void TestFirstKeySelectionChoosesFirstActiveKey()
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

    internal static void TestInitializedEmptyKeySelectionStaysEmpty()
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

}
