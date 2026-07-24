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

    internal static void TestActiveProbeMetricsPreserveReportedLatency()
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

        Assert(provider.FirstTokenLatencyMs == 2_000,
            "Recording a local probe discarded the provider-reported latency needed after TTL expiry.");
        Assert(provider.ActiveProbeFirstTokenLatencyMs == 180, "Active probe latency was not exposed for presentation.");
        Assert(provider.ActiveProbeCheckedAt == now, "Active probe timestamp was not preserved.");
        Assert(provider.OutputTokensPerSecond == 20, "A one-token active probe overwrote reported output speed.");
    }

    internal static void TestActiveProbeObservationsTrackFailureAndRecovery()
    {
        var startedAt = new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(
            startedAt,
            [Provider(1, 0.01, true, 1, startedAt, latency: 2_000, outputTps: 20)],
            new Dictionary<long, double>());

        window.RecordActiveProbeObservations(
        [
            new ActiveProbeObservation(" openai ", 1, startedAt.AddSeconds(1), true, 100),
            new ActiveProbeObservation("openai", 1, startedAt.AddSeconds(2), false)
        ]);
        var failed = window.RecordActiveProbeObservations([]).Providers.Single();

        Assert(failed.ActiveProbeHealthy == false,
            "The latest failed observation did not mark the provider unhealthy.");
        Assert(failed.ActiveProbeCheckedAt == startedAt.AddSeconds(2),
            "The latest failed observation timestamp was not retained.");
        Assert(failed.ActiveProbeSampleCount == 2,
            "Success and failure observations were not both counted.");
        Assert(failed.ActiveProbeFirstTokenLatencyMs == 100 && failed.FirstTokenLatencyMs == 2_000,
            "A failed observation replaced provider or successful local TTFT with fake data.");

        var recovered = window.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 1, startedAt.AddSeconds(3), true, 300)])
            .Providers.Single();

        Assert(recovered.ActiveProbeHealthy == true,
            "A later successful observation did not recover probe health.");
        Assert(recovered.ActiveProbeCheckedAt == startedAt.AddSeconds(3),
            "Recovery did not become the latest probe observation.");
        Assert(recovered.ActiveProbeSampleCount == 3,
            "Recovery was not included in the probe sample count.");
        Assert(recovered.ActiveProbeFirstTokenLatencyMs == 200,
            "Local TTFT did not use the median of successful observations only.");

        var sameTimestamp = startedAt.AddSeconds(4);
        var sameTimestampResult = window.RecordActiveProbeObservations(
        [
            new ActiveProbeObservation("openai", 1, sameTimestamp, true, 500),
            new ActiveProbeObservation("openai", 1, sameTimestamp, false)
        ]).Providers.Single();
        Assert(sameTimestampResult.ActiveProbeHealthy == false,
            "The last recorded observation did not win a same-timestamp health tie.");

        var outOfOrder = window.RecordActiveProbeObservations(
        [
            new ActiveProbeObservation("openai", 1, startedAt.AddSeconds(6), true, 700),
            new ActiveProbeObservation("openai", 1, startedAt.AddSeconds(5), false)
        ]).Providers.Single();
        Assert(outOfOrder.ActiveProbeHealthy == true &&
            outOfOrder.ActiveProbeCheckedAt == startedAt.AddSeconds(6),
            "An older out-of-order observation replaced the newest probe health.");
    }

    internal static void TestActiveProbeLateExpiredSampleCannotPolluteMedian()
    {
        var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        var window = new ProviderMetricsRollingWindow();
        window.Observe(
            now,
            [Provider(1, 0.01, true, 1, now, latency: 2_000)],
            new Dictionary<long, double>());
        window.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 1, now, true, 100)]);

        var snapshot = window.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 1, now.AddMinutes(-31), true, 10_000)]);
        var provider = snapshot.Providers.Single();

        Assert(provider.ActiveProbeSampleCount == 1 &&
            provider.ActiveProbeFirstTokenLatencyMs == 100 &&
            provider.ActiveProbeCheckedAt == now,
            "A late expired probe sample polluted the active-probe rolling window.");
    }

    internal static void TestActiveProbeObservationValidation()
    {
        AssertThrows<ArgumentException>(
            () => new ActiveProbeObservation(" ", 1, DateTimeOffset.UtcNow, true, 1).Validate(),
            "A blank probe platform was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ActiveProbeObservation("openai", 0, DateTimeOffset.UtcNow, true, 1).Validate(),
            "A non-positive probe group was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ActiveProbeObservation("openai", 1, DateTimeOffset.UtcNow, true, double.NaN).Validate(),
            "A non-finite probe TTFT was accepted.");
        AssertThrows<ArgumentException>(
            () => new ActiveProbeObservation("openai", 1, DateTimeOffset.UtcNow, false, 1).Validate(),
            "A failed observation was allowed to carry fake TTFT data.");
    }

    internal static void TestActiveProbePolicyTtlUsesConfiguredInterval()
    {
        Assert(new PersistentAppSettings().CreatePolicy().ActiveProbeMaximumAge is null,
            "Disabled probing unexpectedly enabled a health TTL.");
        Assert(new PersistentAppSettings
        {
            ActiveProbeEnabled = true,
            ActiveProbeIntervalSeconds = 90
        }.CreatePolicy().ActiveProbeMaximumAge == TimeSpan.FromSeconds(180),
            "Probe TTL was not twice the configured interval.");
        Assert(new PersistentAppSettings
        {
            ActiveProbeEnabled = true,
            ActiveProbeIntervalSeconds = 0
        }.CreatePolicy().ActiveProbeMaximumAge == TimeSpan.FromSeconds(180),
            "An invalid interval did not fall back to the safe default.");
        Assert(new PersistentAppSettings
        {
            ActiveProbeEnabled = true,
            ActiveProbeIntervalSeconds = int.MaxValue
        }.CreatePolicy().ActiveProbeMaximumAge == TimeSpan.FromMinutes(30),
            "A huge probe interval bypassed the thirty-minute TTL cap.");
    }

    internal static void TestActiveProbeHttp200JsonErrorIsStructuredAndSafe()
    {
        const string sensitiveDetail = "sensitive-upstream-detail";
        var exception = CaptureProbeProtocolException(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"code\":\"invalid_api_key\",\"message\":\"{sensitiveDetail}\"}}}}",
                Encoding.UTF8,
                "application/json")
        });

        Assert(exception.ApiCode == "invalid_api_key",
            "The safe API code was not retained from an HTTP 200 JSON error.");
        Assert(!exception.Message.Contains(sensitiveDetail, StringComparison.Ordinal),
            "The structured probe exception leaked an upstream error message.");
    }

    internal static void TestActiveProbeStructuredJsonSuffixErrorIsRejected()
    {
        var exception = CaptureProbeProtocolException(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"code\":\"upstream_unavailable\",\"status\":\"error\"}",
                Encoding.UTF8,
                "application/problem+json")
        });

        Assert(exception.ApiCode == "upstream_unavailable",
            "An application/*+json error did not retain its safe API code.");
    }

    internal static void TestActiveProbeSseErrorIsStructuredAndSafe()
    {
        const string sensitiveDetail = "sensitive-stream-detail";
        var exception = CaptureProbeProtocolException(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"data: {{\"error\":{{\"code\":\"rate_limit_exceeded\",\"message\":\"{sensitiveDetail}\"}}}}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        });

        Assert(exception.ApiCode == "rate_limit_exceeded",
            "The safe API code was not retained from an SSE error event.");
        Assert(!exception.Message.Contains(sensitiveDetail, StringComparison.Ordinal),
            "The structured SSE exception leaked an upstream error message.");
    }

    internal static void TestActiveProbeMalformedDataNeverBecomesSuccess()
    {
        var sseException = CaptureProbeProtocolException(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: not-json\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"pong\"}}]}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        });
        Assert(sseException.ApiCode is null,
            "Malformed SSE data invented an API error code.");

        var jsonException = CaptureProbeProtocolException(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        });
        Assert(jsonException.ApiCode is null,
            "Malformed JSON invented an API error code.");
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

    private static ActiveProbeProtocolException CaptureProbeProtocolException(HttpResponseMessage response)
    {
        using var probe = new OpenAiStreamingProbeClient(
            new StubHttpMessageHandler(_ => response));
        try
        {
            probe.ProbeAsync(
                    new ActiveProbeRequest(
                        "https://example.test",
                        "probe-key-value",
                        "probe-model",
                        "openai",
                        1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (ActiveProbeProtocolException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The invalid probe response was accepted as successful.");
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
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
