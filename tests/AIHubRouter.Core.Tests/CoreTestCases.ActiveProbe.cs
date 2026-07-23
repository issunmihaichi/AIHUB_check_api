using System.Net;
using System.Text;
using AIHubRouter.Core;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestActiveProbeMeasuresFirstContentToken()
    {
        var observedRequests = new List<HttpRequestMessage>();
        using var probe = new OpenAiStreamingProbeClient(
            new StubHttpMessageHandler(request =>
            {
                observedRequests.Add(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"content\":\"pong\"}}]}\n\n" +
                        "data: [DONE]\n\n",
                        Encoding.UTF8,
                        "text/event-stream")
                };
            }),
            timestamp: new Queue<long>([100, 225]).Dequeue,
            elapsedMilliseconds: (started, completed) => completed - started);

        var measurement = probe.ProbeAsync(
                new ActiveProbeRequest("https://example.test", "probe-key-value", "probe-model", "openai", 7),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(measurement.GroupId == 7, "Active probe lost the target group.");
        Assert(measurement.FirstTokenLatencyMs == 125, "Active probe did not measure the first content token.");
        Assert(observedRequests.Single().RequestUri?.AbsolutePath == "/v1/chat/completions",
            "Active probe used the wrong upstream endpoint.");
        Assert(observedRequests.Single().Headers.Authorization?.Scheme == "Bearer",
            "Active probe did not send the test API Key as Bearer authentication.");
    }

    internal static void TestActiveProbeChecksSelectedKeyWithoutChangingGroup()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now);
        ActiveProbeRequest? observedRequest = null;
        var upstream = new StubUpstreamProbeClient(request =>
        {
            observedRequest = request;
            return Task.FromResult(new ActiveProbeMeasurement(
                request.Platform,
                request.GroupId,
                now,
                180));
        });
        var service = new ActiveProviderProbeService(upstream, () => now);

        var result = service.CheckSelectedKeyAsync(
                account,
                new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(result.Success && result.Measurement?.GroupId == 1,
            "Selected-Key health check did not return the current Key group measurement.");
        Assert(observedRequest?.GroupId == 1,
            "Selected-Key health check did not probe the selected Key's current group.");
        Assert(account.UpdateCalls == 0,
            "Selected-Key health check changed the selected Key's group.");
    }

    internal static void TestActiveProbeHealthFailureDoesNotChangeSelectedKeyGroup()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now);
        var upstream = new StubUpstreamProbeClient(_ =>
            Task.FromException<ActiveProbeMeasurement>(new HttpRequestException("synthetic probe failure")));
        var service = new ActiveProviderProbeService(upstream, () => now);

        var result = service.CheckSelectedKeyAsync(
                account,
                new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(!result.Success, "A failed selected-Key health check was reported as successful.");
        Assert(account.UpdateCalls == 0,
            "A failed selected-Key health check changed the selected Key's group.");
    }

    internal static void TestActiveProbeRestoresTestKeyAfterCycle()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now)
        {
            ProvidersOverride =
            [
                Provider(2, 0.01, true, 1, now, id: "provider-2"),
                Provider(3, 0.02, true, 1, now, id: "provider-3")
            ],
            GroupsOverride = [Group(1), Group(2), Group(3)]
        };
        var upstream = new StubUpstreamProbeClient(request =>
            Task.FromResult(new ActiveProbeMeasurement(
                request.Platform,
                request.GroupId,
                now,
                request.GroupId == 2 ? 200 : 300)));
        var service = new ActiveProviderProbeService(upstream, () => now);

        var result = service.RunCycleAsync(
                account,
                new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                account.ProvidersOverride,
                account.GroupsOverride,
                ProviderBlocklist.Empty,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(result.Results.Count == 2 && result.Results.All(item => item.Success),
            "Active probe did not test every eligible group.");
        Assert(account.UpdatedGroupIds.SequenceEqual(new long[] { 2, 3, 1 }),
            "Active probe did not restore the test Key to its original group.");
    }

    internal static void TestActiveProbeRestoresTestKeyAfterFailure()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now)
        {
            ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
            GroupsOverride = [Group(1), Group(2)]
        };
        var upstream = new StubUpstreamProbeClient(_ =>
            Task.FromException<ActiveProbeMeasurement>(new HttpRequestException("synthetic probe failure")));
        var service = new ActiveProviderProbeService(upstream, () => now);

        var result = service.RunCycleAsync(
                account,
                new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                account.ProvidersOverride,
                account.GroupsOverride,
                ProviderBlocklist.Empty,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(result.Results.Single().Success is false, "A failed upstream probe was reported as successful.");
        Assert(account.UpdatedGroupIds.SequenceEqual(new long[] { 2, 1 }),
            "The test Key was not restored after an upstream probe failure.");
    }

    internal static void TestActiveProbeRestoresTestKeyAfterCanceledRemoteSwitch()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var account = new StubRoutingClient(now)
        {
            ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
            GroupsOverride = [Group(1), Group(2)],
            AfterRemoteKeyGroupUpdate = (_, groupId, token) =>
            {
                if (groupId == 2)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                }

                if (groupId == 1)
                {
                    Assert(!token.CanBeCanceled,
                        "The canceled active-probe cycle restored the test Key with a cancelable token.");
                }
            }
        };
        var upstream = new StubUpstreamProbeClient(_ =>
            Task.FromResult(new ActiveProbeMeasurement("openai", 2, now, 200)));
        var service = new ActiveProviderProbeService(upstream, () => now);

        var canceled = false;
        try
        {
            service.RunCycleAsync(
                    account,
                    new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                    account.ProvidersOverride,
                    account.GroupsOverride,
                    ProviderBlocklist.Empty,
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            canceled = true;
        }

        Assert(canceled, "The canceled active-probe cycle did not surface cancellation.");
        Assert(account.UpdatedGroupIds.SequenceEqual(new long[] { 2, 1 }),
            "The test Key was not restored after a canceled request whose remote switch succeeded.");
    }

    internal static void TestActiveProbeSurfacesRestoreTimeoutAfterCancellation()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var account = new StubRoutingClient(now)
        {
            ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
            GroupsOverride = [Group(1), Group(2)],
            AfterRemoteKeyGroupUpdate = (_, groupId, token) =>
            {
                if (groupId == 2)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                }

                if (groupId == 1)
                {
                    throw new OperationCanceledException("synthetic restore timeout");
                }
            }
        };
        var upstream = new StubUpstreamProbeClient(_ =>
            Task.FromResult(new ActiveProbeMeasurement("openai", 2, now, 200)));
        var service = new ActiveProviderProbeService(upstream, () => now);

        Exception? failure = null;
        try
        {
            service.RunCycleAsync(
                    account,
                    new ActiveProbeConfiguration("https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                    account.ProvidersOverride,
                    account.GroupsOverride,
                    ProviderBlocklist.Empty,
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert(failure is InvalidOperationException { InnerException: OperationCanceledException },
            "A timeout while restoring the test Key was reported as an ordinary cancellation.");
    }

    internal static void TestActiveProbeMetricsOverrideReportedLatency()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(
            now,
            [Provider(1, 0.01, true, 1, now, latency: 2_000, outputTps: 20)],
            new Dictionary<long, double>());

        var snapshot = window.RecordActiveProbes(
            [new ActiveProbeMeasurement("openai", 1, now, 180)]);
        var provider = snapshot.Providers.Single();

        Assert(provider.FirstTokenLatencyMs == 180, "Fresh active probing did not override reported latency.");
        Assert(provider.ActiveProbeFirstTokenLatencyMs == 180, "Active probe latency was not exposed for presentation.");
        Assert(provider.ActiveProbeCheckedAt == now, "Active probe timestamp was not preserved.");
        Assert(provider.OutputTokensPerSecond == 20, "A one-token active probe overwrote reported output speed.");
    }

    internal static void TestActiveProbeKeyPersistsOnlyInCredentials()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
        const string probeKey = "probe-key-value";
        try
        {
            var store = new AppSettingsStore(directory);
            store.Save(
                new PersistentAppSettings
                {
                    PersistCredentials = true,
                    ActiveProbeEnabled = true,
                    ActiveProbeKeyId = 10,
                    ActiveProbeModel = "probe-model",
                    ActiveProbeIntervalSeconds = 90
                },
                new PersistentCredentials { ActiveProbeApiKey = probeKey });

            var settingsText = File.ReadAllText(Path.Combine(directory, "settings.json"));
            var loaded = store.Load();
            Assert(!settingsText.Contains(probeKey, StringComparison.Ordinal), "Test API Key was written to plain settings.");
            Assert(loaded.Settings.ActiveProbeEnabled && loaded.Settings.ActiveProbeKeyId == 10,
                "Active probe settings did not roundtrip.");
            Assert(loaded.Settings.ActiveProbeIntervalSeconds == 90,
                "Selected-Key health-check interval did not roundtrip.");
            Assert(loaded.Credentials?.ActiveProbeApiKey == probeKey, "Encrypted test API Key did not roundtrip.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static void TestSessionPersistenceRetainsActiveProbeKey()
    {
        var now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var api = new StubRoutingClient(now);
        PersistentCredentials? persisted = null;
        using var service = new RoutingService(
            new PersistentAppSettings { KeySelectionInitialized = true, SelectedKeyIds = [10] },
            new PersistentCredentials
            {
                Email = "user@example.test",
                Password = "synthetic-password",
                ActiveProbeApiKey = "probe-key-value"
            },
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(api),
            persistCredentials: (credentials, _) =>
            {
                persisted = credentials;
                return Task.CompletedTask;
            },
            utcNow: () => now);

        service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

        Assert(persisted?.ActiveProbeApiKey == "probe-key-value",
            "Session refresh dropped the active-probe API Key from encrypted persistence.");
    }
}

internal sealed class StubUpstreamProbeClient(
    Func<ActiveProbeRequest, Task<ActiveProbeMeasurement>> responder) : IUpstreamProbeClient
{
    public Task<ActiveProbeMeasurement> ProbeAsync(ActiveProbeRequest request, CancellationToken cancellationToken) =>
        responder(request);

    public void Dispose()
    {
    }
}
