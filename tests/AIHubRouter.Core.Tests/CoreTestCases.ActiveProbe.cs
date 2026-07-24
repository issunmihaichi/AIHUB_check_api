using System.Net;
using System.Text;
using System.Text.Json;
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

    internal static void TestActiveProbeTargetsThenRestoresDedicatedKey()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var operations = new List<string>();
        var account = new StubRoutingClient(now)
        {
            AfterRemoteKeyGroupUpdate = (keyId, groupId, token) =>
                operations.Add($"key:{keyId}:{groupId}:{token.CanBeCanceled}")
        };
        var upstream = new StubUpstreamProbeClient(request =>
        {
            operations.Add($"probe:{request.GroupId}");
            return Task.FromResult(new ActiveProbeMeasurement(
                request.Platform,
                request.GroupId,
                now,
                125));
        });
        var service = new ActiveProviderProbeService(upstream, () => now);

        var result = service.ProbeTargetAsync(
                account,
                new ActiveProbeConfiguration(
                    "https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                2,
                cancellation.Token)
            .GetAwaiter()
            .GetResult();

        Assert(result.Success && result.GroupId == 2 && result.Measurement?.FirstTokenLatencyMs == 125,
            "The target probe did not return its successful first-token measurement.");
        Assert(operations.SequenceEqual(new[]
            {
                "key:10:2:True",
                "probe:2",
                "key:10:1:False"
            }),
            "The dedicated probe Key was not moved, probed, and restored in order with a non-cancelable restore.");
    }

    internal static void TestActiveProbeRejectsMismatchedMeasurementIdentity()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 5, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now);
        var upstream = new StubUpstreamProbeClient(_ => Task.FromResult(
            new ActiveProbeMeasurement("anthropic", 99, now, 100)));
        var service = new ActiveProviderProbeService(upstream, () => now);

        var failure = CaptureException(() => service.ProbeTargetAsync(
                account,
                new ActiveProbeConfiguration(
                    "https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                2)
            .GetAwaiter()
            .GetResult());

        Assert(failure is InvalidOperationException && failure is not ActiveProbeRestoreException,
            "A measurement for a different platform/group was accepted as target health.");
        Assert(account.UpdatedGroupIds.SequenceEqual(new long[] { 2, 1 }),
            "A mismatched upstream measurement did not restore the dedicated Key.");
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
        AssertThrows<ArgumentException>(
            () => new ActiveProbeObservation("openai", 1, DateTimeOffset.UtcNow, true).Validate(),
            "A successful observation without TTFT data was accepted.");
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
            "The known categorical API code was not retained from an HTTP 200 JSON error.");
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

    internal static void TestActiveProbeProtocolExceptionRedactsCredentialShapedCodes()
    {
        Assert(new ActiveProbeProtocolException("upstream_failed").ApiCode == "upstream_failed",
            "A safe upstream failure code was suppressed.");

        foreach (var credentialShapedCode in new[]
                 {
                     "sk-synthetic-placeholder",
                     "bearer-synthetic-placeholder",
                     "eyJheader.eyJpayload.synthetic-signature",
                     "synthetic_token_failure",
                     "synthetic_secret_failure",
                     "password_rejected",
                     "credential_rejected"
                 })
        {
            var exception = new ActiveProbeProtocolException(credentialShapedCode);
            Assert(exception.ApiCode is null,
                "A credential-shaped API code was exposed by the probe protocol exception.");
        }

        Assert(new ActiveProbeProtocolException("invalid_api_key").ApiCode == "invalid_api_key",
            "A known categorical global API code was treated as credential-shaped data.");
    }

    internal static void TestActiveProbeNonSuccessHttpResponsePreservesStatusAndRedactsBody()
    {
        const string sensitiveBody = "sensitive-upstream-response-body";
        using var probe = new OpenAiStreamingProbeClient(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(sensitiveBody, Encoding.UTF8, "text/plain")
            }));

        var exception = CaptureException(() => probe.ProbeAsync(
                new ActiveProbeRequest("https://example.test", "probe-key-value", "probe-model", "openai", 1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult());

        Assert(exception is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable },
            "A non-success probe response did not preserve its HTTP status code.");
        Assert(!exception.Message.Contains(sensitiveBody, StringComparison.Ordinal),
            "A non-success probe response leaked its body through the exception message.");
    }

    internal static void TestActiveProbeNonSuccessJsonGlobalFailurePreservesStatus()
    {
        var now = new DateTimeOffset(2026, 7, 24, 11, 0, 0, TimeSpan.Zero);
        var configuration = new ActiveProbeConfiguration(
            "https://example.test", "probe-key-value", "probe-model", 10, "openai");

        foreach (var (statusCode, apiCode) in new[]
                 {
                     (HttpStatusCode.BadRequest, "model_not_found"),
                     (HttpStatusCode.NotFound, "invalid_api_key")
                 })
        {
            using var probe = new OpenAiStreamingProbeClient(
                new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        $"{{\"error\":{{\"code\":\"{apiCode}\",\"message\":\"sensitive-upstream-detail\"}}}}",
                        Encoding.UTF8,
                        "application/json")
                }));
            var failure = CaptureException(() => new ActiveProviderProbeService(probe, () => now)
                .CheckSelectedKeyAsync(new StubRoutingClient(now), configuration)
                .GetAwaiter()
                .GetResult());

            Assert(failure is HttpRequestException
            {
                StatusCode: var observedStatus,
                InnerException: ActiveProbeProtocolException { IsGlobalConfigurationFailure: true } protocolException
            } && observedStatus == statusCode,
                "A structured non-success global probe error was converted to a node result or lost its status.");
            Assert(((ActiveProbeProtocolException)((HttpRequestException)failure).InnerException!).ApiCode == apiCode,
                "A structured non-success global probe error did not retain its categorical code.");
            Assert(!failure.Message.Contains("sensitive-upstream-detail", StringComparison.Ordinal),
                "A structured non-success probe error leaked its server detail.");
            Assert(!failure.InnerException!.Message.Contains("sensitive-upstream-detail", StringComparison.Ordinal),
                "The structured non-success probe classification retained its server detail.");
        }
    }

    internal static void TestActiveProbeServiceReturnsSanitizedRecoverableDetails()
    {
        var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        var account = new StubRoutingClient(now);
        var configuration = new ActiveProbeConfiguration(
            "https://example.test", "probe-key-value", "probe-model", 10, "openai");

        var statusResult = new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(
                    new HttpRequestException("sensitive", null, HttpStatusCode.TooManyRequests))),
                () => now)
            .CheckSelectedKeyAsync(account, configuration)
            .GetAwaiter()
            .GetResult();
        Assert(!statusResult.Success && statusResult.Detail == "http-429",
            "A recoverable HTTP probe failure did not expose only its safe status detail.");

        var transportResult = new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(
                    new HttpRequestException("synthetic transport failure"))),
                () => now)
            .CheckSelectedKeyAsync(account, configuration)
            .GetAwaiter()
            .GetResult();
        Assert(!transportResult.Success && transportResult.Detail == "probe-failed",
            "A status-less transport failure did not receive the generic probe detail.");

        var codeResult = new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(
                    new ActiveProbeProtocolException("upstream_failed"))),
                () => now)
            .CheckSelectedKeyAsync(account, configuration)
            .GetAwaiter()
            .GetResult();
        Assert(!codeResult.Success && codeResult.Detail == "api:upstream_failed",
            "A recoverable safe API code was not retained in the probe result detail.");

        var redactedResult = new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(
                    new ActiveProbeProtocolException("sk-synthetic-placeholder"))),
                () => now)
            .CheckSelectedKeyAsync(account, configuration)
            .GetAwaiter()
            .GetResult();
        Assert(!redactedResult.Success && redactedResult.Detail == "probe-failed",
            "A credential-shaped API code was exposed in a failed probe result.");
    }

    internal static void TestActiveProbeServicePropagatesGlobalFailures()
    {
        var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        var configuration = new ActiveProbeConfiguration(
            "https://example.test", "probe-key-value", "probe-model", 10, "openai");
        var globalFailures = new Exception[]
        {
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden),
            new ActiveProbeProtocolException("invalid_api_key"),
            new ActiveProbeProtocolException("authentication_error"),
            new ActiveProbeProtocolException("unauthorized"),
            new ActiveProbeProtocolException("forbidden"),
            new ActiveProbeProtocolException("model_not_found"),
            new ActiveProbeProtocolException("invalid_model")
        };

        foreach (var globalFailure in globalFailures)
        {
            var selectedFailure = CaptureException(() => new ActiveProviderProbeService(
                    new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(globalFailure)),
                    () => now)
                .CheckSelectedKeyAsync(new StubRoutingClient(now), configuration)
                .GetAwaiter()
                .GetResult());
            Assert(ReferenceEquals(selectedFailure, globalFailure),
                "A global selected-Key probe failure was converted to a node result.");

            var account = new StubRoutingClient(now)
            {
                ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
                GroupsOverride = [Group(1), Group(2)]
            };
            var cycleFailure = CaptureException(() => new ActiveProviderProbeService(
                    new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(globalFailure)),
                    () => now)
                .RunCycleAsync(account, configuration, account.ProvidersOverride, account.GroupsOverride, ProviderBlocklist.Empty)
                .GetAwaiter()
                .GetResult());
            Assert(ReferenceEquals(cycleFailure, globalFailure),
                "A global cycle probe failure was converted to a node result.");
        }
    }

    internal static void TestActiveProbeServicePropagatesAccountFailuresAndCancellation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        var configuration = new ActiveProbeConfiguration(
            "https://example.test", "probe-key-value", "probe-model", 10, "openai");
        var upstream = new StubUpstreamProbeClient(_ =>
            Task.FromResult(new ActiveProbeMeasurement("openai", 2, now, 100)));

        var keyReadFailure = new InvalidOperationException("synthetic key read failure");
        var selectedFailure = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .CheckSelectedKeyAsync(new ThrowingActiveProbeAccountClient(keyReadFailure, null), configuration)
            .GetAwaiter()
            .GetResult());
        Assert(ReferenceEquals(selectedFailure, keyReadFailure),
            "A selected-Key account failure was converted to a node result.");

        var cycleReadFailure = new InvalidOperationException("synthetic cycle key read failure");
        var cycleReadException = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .RunCycleAsync(
                new ThrowingActiveProbeAccountClient(cycleReadFailure, null),
                configuration,
                [Provider(2, 0.01, true, 1, now)],
                [Group(1), Group(2)],
                ProviderBlocklist.Empty)
            .GetAwaiter()
            .GetResult());
        Assert(ReferenceEquals(cycleReadException, cycleReadFailure),
            "A cycle key-read failure was converted to a node result.");

        var updateFailure = new InvalidOperationException("synthetic key update failure");
        var cycleUpdateException = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .RunCycleAsync(
                new ThrowingActiveProbeAccountClient(null, updateFailure),
                configuration,
                [Provider(2, 0.01, true, 1, now)],
                [Group(1), Group(2)],
                ProviderBlocklist.Empty)
            .GetAwaiter()
            .GetResult());
        Assert(ReferenceEquals(cycleUpdateException, updateFailure),
            "A cycle key-group update failure was converted to a node result.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .CheckSelectedKeyAsync(new StubRoutingClient(now), configuration, cancellation.Token)
            .GetAwaiter()
            .GetResult());
        Assert(canceled is OperationCanceledException,
            "A caller cancellation was converted to a selected-Key node result.");
    }

    internal static void TestActiveProbeSelectedKeyCancellationWinsRecoverableFailure()
    {
        var now = new DateTimeOffset(2026, 7, 24, 11, 30, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var upstream = new StubUpstreamProbeClient(_ =>
        {
            cancellation.Cancel();
            return Task.FromException<ActiveProbeMeasurement>(
                new HttpRequestException("synthetic post-cancellation transport failure"));
        });

        var failure = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .CheckSelectedKeyAsync(
                new StubRoutingClient(now),
                new ActiveProbeConfiguration(
                    "https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                cancellation.Token)
            .GetAwaiter()
            .GetResult());

        Assert(failure is OperationCanceledException,
            "A selected-Key caller cancellation lost to a recoverable transport failure.");
    }

    internal static void TestActiveProbeCycleCancellationWinsRecoverableFailureAndRestoresKey()
    {
        var now = new DateTimeOffset(2026, 7, 24, 11, 30, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var account = new StubRoutingClient(now)
        {
            ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
            GroupsOverride = [Group(1), Group(2)],
            AfterRemoteKeyGroupUpdate = (_, groupId, token) =>
            {
                if (groupId == 1)
                {
                    Assert(!token.CanBeCanceled,
                        "A canceled probe cycle restored its test Key with the caller token.");
                }
            }
        };
        var upstream = new StubUpstreamProbeClient(_ =>
        {
            cancellation.Cancel();
            return Task.FromException<ActiveProbeMeasurement>(
                new IOException("synthetic post-cancellation stream failure"));
        });

        var failure = CaptureException(() => new ActiveProviderProbeService(upstream, () => now)
            .RunCycleAsync(
                account,
                new ActiveProbeConfiguration(
                    "https://example.test", "probe-key-value", "probe-model", 10, "openai"),
                account.ProvidersOverride,
                account.GroupsOverride,
                ProviderBlocklist.Empty,
                cancellation.Token)
            .GetAwaiter()
            .GetResult());

        Assert(failure is OperationCanceledException,
            "A cycle caller cancellation lost to a recoverable stream failure.");
        Assert(account.UpdatedGroupIds.SequenceEqual(new long[] { 2, 1 }),
            "A canceled cycle did not restore the test Key after a recoverable failure race.");
    }

    internal static void TestActiveProbeServicePropagatesLocalConfigurationFailures()
    {
        var now = new DateTimeOffset(2026, 7, 24, 11, 0, 0, TimeSpan.Zero);
        var configuration = new ActiveProbeConfiguration(
            "https://example.test", "probe-key-value", "probe-model", 10, "openai");
        var localFailure = new FormatException("synthetic local configuration failure");

        var selectedFailure = CaptureException(() => new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(localFailure)),
                () => now)
            .CheckSelectedKeyAsync(new StubRoutingClient(now), configuration)
            .GetAwaiter()
            .GetResult());
        Assert(ReferenceEquals(selectedFailure, localFailure),
            "A local selected-Key configuration failure was converted to a node result.");

        var account = new StubRoutingClient(now)
        {
            ProvidersOverride = [Provider(2, 0.01, true, 1, now)],
            GroupsOverride = [Group(1), Group(2)]
        };
        var cycleFailure = CaptureException(() => new ActiveProviderProbeService(
                new StubUpstreamProbeClient(_ => Task.FromException<ActiveProbeMeasurement>(localFailure)),
                () => now)
            .RunCycleAsync(account, configuration, account.ProvidersOverride, account.GroupsOverride, ProviderBlocklist.Empty)
            .GetAwaiter()
            .GetResult());
        Assert(ReferenceEquals(cycleFailure, localFailure),
            "A local cycle configuration failure was converted to a node result.");
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

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The operation completed without the expected failure.");
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

internal sealed class ThrowingActiveProbeAccountClient(Exception? getKeysFailure, Exception? updateFailure) : IAIHubApiClient
{
    private int _updateCalls;

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default)
    {
        if (getKeysFailure is not null)
        {
            throw getKeysFailure;
        }

        return Task.FromResult<IReadOnlyList<ApiKeyInfo>>(
        [new ApiKeyInfo { Id = 10, Status = "active", GroupId = 1 }]);
    }

    public Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default)
    {
        if (updateFailure is not null && _updateCalls++ == 0)
        {
            throw updateFailure;
        }

        return Task.FromResult(new ApiKeyInfo { Id = keyId, Status = "active", GroupId = groupId });
    }

    public void Dispose()
    {
    }
}
