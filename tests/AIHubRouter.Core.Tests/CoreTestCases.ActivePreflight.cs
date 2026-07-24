using System.Text.Json;
using AIHubRouter.Core;
using static AIHubRouter.Core.Tests.TestFixtures;

namespace AIHubRouter.Core.Tests;

internal static partial class CoreTestCases
{
    internal static void TestRoutingPreflightRestoresHealthKeyBeforeBusinessWrite()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var operations = new List<string>();
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, token) =>
                operations.Add($"key:{keyId}:{groupId}:{token.CanBeCanceled}")
        };
        var upstream = new TrackingUpstreamProbeClient((request, _) =>
        {
            operations.Add($"probe:{request.GroupId}");
            return Task.FromResult(new ActiveProbeMeasurement(
                request.Platform,
                request.GroupId,
                now,
                120));
        }, () => operations.Add("dispose"));
        var factoryCalls = 0;
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () =>
            {
                factoryCalls++;
                return upstream;
            });

        var result = service.RunOnceAsync(cancellationToken: cancellation.Token)
            .GetAwaiter()
            .GetResult();

        Assert(result.ChangedKeyCount == 1 && result.Decision.Target?.Group.Id == 2,
            "A successful target preflight did not permit the business Key write.");
        Assert(factoryCalls == 1 && upstream.Disposed,
            "The live upstream probe client was not created and disposed exactly once.");
        Assert(operations.SequenceEqual(new[]
            {
                "key:99:2:True",
                "probe:2",
                "key:99:1:False",
                "dispose",
                "key:10:2:True"
            }),
            "The health Key was not restored before the business Key write.");
    }

    internal static void TestRoutingPreflightSkipsDryRun()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 5, 0, TimeSpan.Zero);
        var updates = 0;
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (_, _, _) => updates++
        };
        var factoryCalls = 0;
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () =>
            {
                factoryCalls++;
                return new TrackingUpstreamProbeClient((_, _) =>
                    throw new InvalidOperationException("Dry-run created a live probe."));
            });

        var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();

        Assert(result.KeyResults.Single().Changed,
            "Dry-run did not retain the proposed business Key change.");
        Assert(factoryCalls == 0 && updates == 0,
            "Dry-run created a live probe or issued a Key-group PUT.");
    }

    internal static void TestRoutingPreflightFallsBackAfterRecoverableFailure()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 10, 0, TimeSpan.Zero);
        var updates = new List<(long KeyId, long GroupId)>();
        var account = new PreflightRoutingClient(
            [
                Provider(2, 0.01, true, 1, now, id: "provider-2"),
                Provider(3, 0.02, true, 1, now, id: "provider-3")
            ],
            [Group(1), Group(2), Group(3)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, _) => updates.Add((keyId, groupId))
        };
        var upstream = new TrackingUpstreamProbeClient((request, _) =>
            request.GroupId == 2
                ? Task.FromException<ActiveProbeMeasurement>(
                    new HttpRequestException("sensitive target failure"))
                : Task.FromResult(new ActiveProbeMeasurement(
                    request.Platform,
                    request.GroupId,
                    now,
                    180)));
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(upstream.Requests.Select(request => request.GroupId).SequenceEqual(new long[] { 2, 3 }),
            "Routing did not probe each fallback group exactly once in candidate order.");
        Assert(updates.SequenceEqual(new[]
            {
                (99L, 2L),
                (99L, 1L),
                (99L, 3L),
                (99L, 1L),
                (10L, 3L)
            }),
            "Routing wrote a failed target or did not restore the health Key between candidates.");
        Assert(result.Decision.Target?.Group.Id == 3 && result.ChangedKeyCount == 1,
            "Routing did not write business Keys only to the successful fallback target.");
        Assert(result.Providers.Single(provider => provider.GroupId == 2).ActiveProbeHealthy == false &&
            result.Providers.Single(provider => provider.GroupId == 3).ActiveProbeHealthy == true,
            "Routing did not retain failed and successful target observations in shared metrics.");
    }

    internal static void TestRoutingPreflightAllFailuresSaveOnlyFinalState()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 20, 0, TimeSpan.Zero);
        var updates = new List<(long KeyId, long GroupId)>();
        var initialState = new RouteState
        {
            CurrentGroupId = 1,
            CompletedPolicyEvaluationsSinceLastSwitch = 5,
            PendingPolicyTargetGroupId = 2,
            PendingPolicyTargetObservations = 1
        };
        var stateStore = new RecordingRouteStateStore(initialState);
        var account = new PreflightRoutingClient(
            [
                Provider(1, 0.03, true, 1, now, id: "provider-1"),
                Provider(2, 0.01, true, 1, now, id: "provider-2-a"),
                Provider(2, 0.011, true, 1, now, id: "provider-2-b"),
                Provider(3, 0.02, true, 1, now, id: "provider-3")
            ],
            [Group(1), Group(2), Group(3)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, _) => updates.Add((keyId, groupId))
        };
        var upstream = new TrackingUpstreamProbeClient((_, _) =>
            Task.FromException<ActiveProbeMeasurement>(new HttpRequestException("target unavailable")));
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            stateStore,
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(upstream.Requests.Select(request => request.GroupId).SequenceEqual(new long[] { 2, 3 }),
            "Duplicate provider rows caused a group to be probed more than once.");
        Assert(updates.All(update => update.KeyId == 99) && result.ChangedKeyCount == 0,
            "An all-failed preflight cycle wrote a business Key.");
        Assert(stateStore.SaveCalls == 1 && stateStore.Load().CurrentGroupId == 1,
            "The all-failed cycle did not save exactly one final safe route state.");
        Assert(stateStore.Load().CompletedPolicyEvaluationsSinceLastSwitch == 6,
            "Candidate recomputations inflated or discarded the final hysteresis evaluation count.");
    }

    internal static void TestRoutingPreflightRestoreFailureBlocksBusinessWrites()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 25, 0, TimeSpan.Zero);
        var restoreFailure = new InvalidOperationException("synthetic restore failure");
        var businessWrites = 0;
        var stateStore = new RecordingRouteStateStore();
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, _) =>
            {
                if (keyId == 99 && groupId == 1)
                {
                    throw restoreFailure;
                }

                if (keyId == 10)
                {
                    businessWrites++;
                }
            }
        };
        var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            stateStore,
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var failure = CaptureException(() => service.RunOnceAsync().GetAwaiter().GetResult());

        Assert(failure is ActiveProbeRestoreException && ReferenceEquals(failure.InnerException, restoreFailure),
            "A health-Key restore failure was not surfaced as ActiveProbeRestoreException.");
        Assert(businessWrites == 0 && stateStore.SaveCalls == 0,
            "A restore failure wrote a business Key or persisted an unconfirmed route state.");
    }

    internal static void TestRoutingPreflightRejectsUnconfirmedForwardMove()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 27, 0, TimeSpan.Zero);
        var responseKinds = new[] { "null", "wrong-key", "wrong-group" };

        foreach (var responseKind in responseKinds)
        {
            using var cancellation = new CancellationTokenSource();
            var operations = new List<(long KeyId, long GroupId, bool CanBeCanceled)>();
            var stateStore = new RecordingRouteStateStore();
            var metrics = new ProviderMetricsRollingWindow();
            var account = CreateSingleTargetAccount(
                now,
                (keyId, groupId, token) => operations.Add((keyId, groupId, token.CanBeCanceled)));
            account.UpdateResult = (keyId, groupId, updated) => groupId == 2
                ? CreateUnconfirmedKeyMoveResponse(responseKind, keyId, groupId)
                : updated;
            var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
                new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
            using var service = new RoutingService(
                ActivePreflightSettings(),
                ActivePreflightCredentials(),
                stateStore,
                new StubRoutingClientFactory(account),
                utcNow: () => now,
                providerMetrics: metrics,
                upstreamProbeFactory: () => upstream);

            var failure = CaptureException(() => service.RunOnceAsync(cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult());
            var provider = metrics.RecordActiveProbeObservations([]).Providers.Single();

            Assert(failure is InvalidOperationException and not ActiveProbeRestoreException,
                $"The {responseKind} forward response did not propagate as a control-plane failure.");
            Assert(upstream.Requests.Count == 0,
                $"The {responseKind} forward response reached the upstream probe.");
            Assert(operations.SequenceEqual(new[]
                {
                    (99L, 2L, true),
                    (99L, 1L, false)
                }),
                $"The {responseKind} forward response was not followed by a non-cancelable restore only.");
            Assert(stateStore.SaveCalls == 0 && provider.ActiveProbeHealthy is null,
                $"The {responseKind} forward response persisted route state or recorded node health.");
        }
    }

    internal static void TestRoutingPreflightRejectsUnconfirmedRestore()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 28, 0, TimeSpan.Zero);
        var responseKinds = new[] { "null", "wrong-key", "wrong-group" };

        foreach (var responseKind in responseKinds)
        {
            using var cancellation = new CancellationTokenSource();
            var operations = new List<string>();
            var stateStore = new RecordingRouteStateStore();
            var metrics = new ProviderMetricsRollingWindow();
            var account = CreateSingleTargetAccount(
                now,
                (keyId, groupId, token) =>
                    operations.Add($"key:{keyId}:{groupId}:{token.CanBeCanceled}"));
            account.UpdateResult = (keyId, groupId, updated) => groupId == 1
                ? CreateUnconfirmedKeyMoveResponse(responseKind, keyId, groupId)
                : updated;
            var upstream = new TrackingUpstreamProbeClient((request, _) =>
            {
                operations.Add($"probe:{request.GroupId}");
                return Task.FromResult(new ActiveProbeMeasurement(
                    request.Platform,
                    request.GroupId,
                    now,
                    100));
            });
            using var service = new RoutingService(
                ActivePreflightSettings(),
                ActivePreflightCredentials(),
                stateStore,
                new StubRoutingClientFactory(account),
                utcNow: () => now,
                providerMetrics: metrics,
                upstreamProbeFactory: () => upstream);

            var failure = CaptureException(() => service.RunOnceAsync(cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult());
            var provider = metrics.RecordActiveProbeObservations([]).Providers.Single();

            Assert(failure is ActiveProbeRestoreException { InnerException: InvalidOperationException },
                $"The {responseKind} restore response was not surfaced as ActiveProbeRestoreException.");
            Assert(operations.SequenceEqual(new[]
                {
                    "key:99:2:True",
                    "probe:2",
                    "key:99:1:False"
                }),
                $"The {responseKind} restore response was followed by a business write or used the caller token.");
            Assert(stateStore.SaveCalls == 0 && provider.ActiveProbeHealthy is null,
                $"The {responseKind} restore response persisted route state or recorded node health.");
        }
    }

    internal static void TestRoutingPreflightCancellationRestoresWithoutHealthFailure()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 30, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var updates = new List<(long KeyId, long GroupId, bool CanBeCanceled)>();
        var metrics = new ProviderMetricsRollingWindow();
        var stateStore = new RecordingRouteStateStore();
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, token) =>
                updates.Add((keyId, groupId, token.CanBeCanceled))
        };
        var upstream = new TrackingUpstreamProbeClient((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ActiveProbeMeasurement>(token);
        });
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            stateStore,
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            providerMetrics: metrics,
            upstreamProbeFactory: () => upstream);

        var failure = CaptureException(() => service.RunOnceAsync(cancellationToken: cancellation.Token)
            .GetAwaiter()
            .GetResult());
        var provider = metrics.RecordActiveProbeObservations([]).Providers.Single();

        Assert(failure is OperationCanceledException,
            "Caller cancellation was converted into a target health failure.");
        Assert(updates.SequenceEqual(new[]
            {
                (99L, 2L, true),
                (99L, 1L, false)
            }),
            "Cancellation did not restore the health Key with a non-cancelable token before aborting.");
        Assert(provider.ActiveProbeHealthy is null && stateStore.SaveCalls == 0 && upstream.Disposed,
            "Cancellation recorded a node failure, persisted route state, or leaked the probe client.");
    }

    internal static void TestRoutingPreflightHealthKeyAlreadyOnTargetAvoidsControlPlaneMove()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 35, 0, TimeSpan.Zero);
        var updates = new List<(long KeyId, long GroupId)>();
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 2 }
            ])
        {
            AfterUpdate = (keyId, groupId, _) => updates.Add((keyId, groupId))
        };
        var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 90)));
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(upstream.Requests.Single().GroupId == 2 && result.ChangedKeyCount == 1,
            "A health Key already on target was not probed before the business write.");
        Assert(updates.SequenceEqual(new[] { (10L, 2L) }),
            "A health Key already on target received an unnecessary control-plane PUT.");
    }

    internal static void TestRoutingPreflightSkipsNoTargetAndNoRequiredBusinessWrite()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 40, 0, TimeSpan.Zero);

        var noWriteUpdates = 0;
        var noWriteFactoryCalls = 0;
        var noWriteState = new MemoryRouteStateStore();
        noWriteState.Save(new RouteState { CurrentGroupId = 1 });
        var noWriteAccount = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 2 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (_, _, _) => noWriteUpdates++
        };
        using (var service = new RoutingService(
            new PersistentAppSettings
            {
                BaseUrl = "not-a-url",
                Platform = " ",
                KeySelectionInitialized = true,
                SelectedKeyIds = [10],
                ActiveProbeEnabled = true,
                ActiveProbeKeyId = null,
                ActiveProbeModel = ""
            },
            new PersistentCredentials { BearerToken = "synthetic-access" },
            noWriteState,
            new StubRoutingClientFactory(noWriteAccount),
            utcNow: () => now,
            upstreamProbeFactory: () =>
            {
                noWriteFactoryCalls++;
                return new TrackingUpstreamProbeClient((_, _) =>
                    throw new InvalidOperationException("No-write cycle created a probe."));
            }))
        {
            var result = service.RunOnceAsync().GetAwaiter().GetResult();
            Assert(result.Decision.Target?.Group.Id == 2 && result.ChangedKeyCount == 0,
                "The stale route state overrode the observed already-target business Key.");
        }

        var noTargetUpdates = 0;
        var noTargetFactoryCalls = 0;
        var noTargetAccount = new PreflightRoutingClient(
            [Provider(2, 0.01, false, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (_, _, _) => noTargetUpdates++
        };
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(noTargetAccount),
            utcNow: () => now,
            upstreamProbeFactory: () =>
            {
                noTargetFactoryCalls++;
                return new TrackingUpstreamProbeClient((_, _) =>
                    throw new InvalidOperationException("No-target cycle created a probe."));
            }))
        {
            var result = service.RunOnceAsync().GetAwaiter().GetResult();
            Assert(result.Decision.Target is null, "The no-candidate cycle invented a target.");
        }

        Assert(noWriteFactoryCalls == 0 && noWriteUpdates == 0 &&
            noTargetFactoryCalls == 0 && noTargetUpdates == 0,
            "A no-write or no-target cycle created a probe or issued a Key-group PUT.");
    }

    internal static void TestRoutingPreflightUsesCachedHealthAtInclusiveTtlBoundary()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 45, 0, TimeSpan.Zero);
        var ttlBoundary = now.AddSeconds(-180);

        var successMetrics = new ProviderMetricsRollingWindow();
        successMetrics.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 2, ttlBoundary, true, 100)]);
        var successFactoryCalls = 0;
        var successUpdates = new List<(long KeyId, long GroupId)>();
        var successAccount = CreateSingleTargetAccount(now, (keyId, groupId, _) =>
            successUpdates.Add((keyId, groupId)));
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(successAccount),
            utcNow: () => now,
            providerMetrics: successMetrics,
            upstreamProbeFactory: () =>
            {
                successFactoryCalls++;
                return new TrackingUpstreamProbeClient((_, _) =>
                    throw new InvalidOperationException("Fresh success was probed again."));
            }))
        {
            var result = service.RunOnceAsync().GetAwaiter().GetResult();
            Assert(result.ChangedKeyCount == 1, "Fresh cached success did not permit the business write.");
        }
        Assert(successFactoryCalls == 0 && successUpdates.SequenceEqual(new[] { (10L, 2L) }),
            "Fresh cached success moved the health Key or triggered a live probe.");

        var failureMetrics = new ProviderMetricsRollingWindow();
        failureMetrics.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 2, ttlBoundary, false)]);
        var failureFactoryCalls = 0;
        var failureUpdates = 0;
        var failureAccount = CreateSingleTargetAccount(now, (_, _, _) => failureUpdates++);
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(failureAccount),
            utcNow: () => now,
            providerMetrics: failureMetrics,
            upstreamProbeFactory: () =>
            {
                failureFactoryCalls++;
                return new TrackingUpstreamProbeClient((_, _) =>
                    throw new InvalidOperationException("Fresh failure was probed again."));
            }))
        {
            var result = service.RunOnceAsync().GetAwaiter().GetResult();
            Assert(result.Decision.Target is null, "Fresh cached failure remained route eligible.");
        }
        Assert(failureFactoryCalls == 0 && failureUpdates == 0,
            "Fresh cached failure was retried or written.");

        var expiredMetrics = new ProviderMetricsRollingWindow();
        expiredMetrics.RecordActiveProbeObservations(
            [new ActiveProbeObservation("openai", 2, ttlBoundary.AddSeconds(-1), false)]);
        var expiredUpstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 110)));
        var expiredUpdates = new List<(long KeyId, long GroupId)>();
        var expiredAccount = CreateSingleTargetAccount(now, (keyId, groupId, _) =>
            expiredUpdates.Add((keyId, groupId)));
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(expiredAccount),
            utcNow: () => now,
            providerMetrics: expiredMetrics,
            upstreamProbeFactory: () => expiredUpstream))
        {
            var result = service.RunOnceAsync().GetAwaiter().GetResult();
            Assert(result.ChangedKeyCount == 1, "Expired cached failure did not return to neutral eligibility.");
        }
        Assert(expiredUpstream.Requests.Single().GroupId == 2 &&
            expiredUpdates.SequenceEqual(new[] { (99L, 2L), (99L, 1L), (10L, 2L) }),
            "Expired cached failure did not trigger a fresh target preflight.");
    }

    internal static void TestRoutingPreflightReconcilesMixedBusinessGroupsWithoutSwitch()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 50, 0, TimeSpan.Zero);
        var updates = new List<(long KeyId, long GroupId)>();
        var account = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key 10", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 11, Name = "Business Key 11", Status = "active", GroupId = 2 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = (keyId, groupId, _) => updates.Add((keyId, groupId))
        };
        var stateStore = new MemoryRouteStateStore();
        stateStore.Save(new RouteState { CurrentGroupId = 2 });
        var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
        using var service = new RoutingService(
            ActivePreflightSettings(10, 11),
            ActivePreflightCredentials(),
            stateStore,
            new StubRoutingClientFactory(account),
            utcNow: () => now,
            upstreamProbeFactory: () => upstream);

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(!result.Decision.ShouldSwitch && result.Decision.Target?.Group.Id == 2,
            "Mixed business groups unexpectedly required a policy switch decision.");
        Assert(upstream.Requests.Single().GroupId == 2 && updates.SequenceEqual(new[]
            {
                (99L, 2L),
                (99L, 1L),
                (10L, 2L)
            }),
            "Mixed business groups were reconciled without preflighting the unchanged target.");
    }

    internal static void TestRoutingPreflightInvalidConfigurationFailsClosed()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 55, 0, TimeSpan.Zero);
        var invalidConfigurations = new (PersistentAppSettings Settings, PersistentCredentials Credentials)[]
        {
            (
                CreatePreflightSettings("https://example.test", "openai", null, "probe-model"),
                ActivePreflightCredentials()),
            (
                CreatePreflightSettings("https://example.test", "openai", 99, " "),
                ActivePreflightCredentials()),
            (
                CreatePreflightSettings("not-a-url", "openai", 99, "probe-model"),
                ActivePreflightCredentials()),
            (
                CreatePreflightSettings("https://example.test", " ", 99, "probe-model"),
                ActivePreflightCredentials()),
            (
                CreatePreflightSettings("https://example.test", "openai", 99, "probe-model"),
                new PersistentCredentials { BearerToken = "synthetic-access", ActiveProbeApiKey = " " })
        };

        foreach (var (settings, credentials) in invalidConfigurations)
        {
            var updates = 0;
            var factoryCalls = 0;
            var metrics = new ProviderMetricsRollingWindow();
            var stateStore = new RecordingRouteStateStore();
            var account = CreateSingleTargetAccount(now, (_, _, _) => updates++);
            using var service = new RoutingService(
                settings,
                credentials,
                stateStore,
                new StubRoutingClientFactory(account),
                utcNow: () => now,
                providerMetrics: metrics,
                upstreamProbeFactory: () =>
                {
                    factoryCalls++;
                    return new TrackingUpstreamProbeClient((_, _) =>
                        throw new InvalidOperationException("Invalid configuration created a probe."));
                });

            var failure = CaptureException(() => service.RunOnceAsync().GetAwaiter().GetResult());
            var provider = metrics.RecordActiveProbeObservations([]).Providers.Single();

            Assert(failure is ArgumentException,
                "Invalid active-preflight configuration did not fail with a local validation exception.");
            Assert(factoryCalls == 0 && updates == 0 && stateStore.SaveCalls == 0,
                "Invalid configuration created a probe, wrote a Key, or persisted route state.");
            Assert(provider.ActiveProbeHealthy is null,
                "Invalid local configuration was recorded as a target node failure.");
        }
    }

    internal static void TestRoutingPreflightAccountErrorsPropagateWithoutNodeFailure()
    {
        var now = new DateTimeOffset(2026, 7, 24, 14, 0, 0, TimeSpan.Zero);

        var queryFailure = new InvalidOperationException("synthetic account Key query failure");
        var queryMetrics = new ProviderMetricsRollingWindow();
        var queryUpdates = 0;
        var queryAccount = CreateSingleTargetAccount(now, (_, _, _) => queryUpdates++);
        queryAccount.GetKeysFailure = call => call == 2 ? queryFailure : null;
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new RecordingRouteStateStore(),
            new StubRoutingClientFactory(queryAccount),
            utcNow: () => now,
            providerMetrics: queryMetrics,
            upstreamProbeFactory: () => new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
                new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)))))
        {
            var failure = CaptureException(() => service.RunOnceAsync().GetAwaiter().GetResult());
            var provider = queryMetrics.RecordActiveProbeObservations([]).Providers.Single();
            Assert(ReferenceEquals(failure, queryFailure) && queryUpdates == 0,
                "Account Key query failure was converted or followed by a Key write.");
            Assert(provider.ActiveProbeHealthy is null,
                "Account Key query failure was recorded as a target node failure.");
        }

        var updateFailure = new AIHubApiException(
            "Authentication required.",
            System.Net.HttpStatusCode.Unauthorized,
            "401");
        var updateMetrics = new ProviderMetricsRollingWindow();
        var updateOperations = new List<(long KeyId, long GroupId)>();
        var updateAccount = CreateSingleTargetAccount(now, (keyId, groupId, _) =>
        {
            updateOperations.Add((keyId, groupId));
            if (keyId == 99)
            {
                throw updateFailure;
            }
        });
        var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
        using (var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new RecordingRouteStateStore(),
            new StubRoutingClientFactory(updateAccount),
            utcNow: () => now,
            providerMetrics: updateMetrics,
            upstreamProbeFactory: () => upstream))
        {
            var failure = CaptureException(() => service.RunOnceAsync().GetAwaiter().GetResult());
            var provider = updateMetrics.RecordActiveProbeObservations([]).Providers.Single();
            Assert(ReferenceEquals(failure, updateFailure),
                "Account authentication failure was wrapped or converted into target health.");
            Assert(updateOperations.SequenceEqual(new[] { (99L, 2L) }) &&
                upstream.Requests.Count == 0,
                "Definitive account authentication failure was restored or reached the upstream/business write.");
            Assert(provider.ActiveProbeHealthy is null,
                "Account Key update failure was recorded as a target node failure.");
        }
    }

    internal static void TestRoutingPreflightIgnoresForeignPlatformCachedSuccess()
    {
        var now = new DateTimeOffset(2026, 7, 24, 14, 5, 0, TimeSpan.Zero);
        var foreignProvider = new ProviderStatus
        {
            Id = "foreign-provider-2",
            GroupId = 2,
            PlanType = "Foreign",
            Platform = "anthropic",
            PriceMultiplier = 0.01,
            Available = true,
            Enabled = true,
            CheckedAt = now,
            FirstTokenLatencyMs = 100,
            OutputTokensPerSecond = 20,
            SuccessRates = new Dictionary<string, double> { ["6h"] = 1 }
        };
        var metrics = new ProviderMetricsRollingWindow();
        metrics.RecordActiveProbeObservations(
            [new ActiveProbeObservation("anthropic", 2, now, true, 80)]);
        var factoryCalls = 0;
        var upstream = new TrackingUpstreamProbeClient((request, _) => Task.FromResult(
            new ActiveProbeMeasurement(request.Platform, request.GroupId, now, 100)));
        var providerAccount = new PreflightRoutingClient(
            [Provider(2, 0.01, true, 1, now), foreignProvider],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ]);
        using var service = new RoutingService(
            ActivePreflightSettings(),
            ActivePreflightCredentials(),
            new MemoryRouteStateStore(),
            new StubRoutingClientFactory(providerAccount),
            utcNow: () => now,
            providerMetrics: metrics,
            upstreamProbeFactory: () =>
            {
                factoryCalls++;
                return upstream;
            });

        var result = service.RunOnceAsync().GetAwaiter().GetResult();

        Assert(factoryCalls == 1 && upstream.Requests.Single().GroupId == 2 && result.ChangedKeyCount == 1,
            "A foreign platform's cached success incorrectly bypassed the target platform preflight.");
    }

    private static PersistentAppSettings ActivePreflightSettings(params long[] selectedKeyIds) => new()
    {
        BaseUrl = "https://example.test",
        Platform = "openai",
        RoutingMode = RoutingMode.Economy,
        KeySelectionInitialized = true,
        SelectedKeyIds = selectedKeyIds.Length == 0 ? [10] : selectedKeyIds,
        ActiveProbeEnabled = true,
        ActiveProbeKeyId = 99,
        ActiveProbeModel = "probe-model",
        ActiveProbeIntervalSeconds = 90
    };

    private static PersistentCredentials ActivePreflightCredentials() => new()
    {
        BearerToken = "synthetic-access",
        ActiveProbeApiKey = "probe-key-value"
    };

    private static PersistentAppSettings CreatePreflightSettings(
        string baseUrl,
        string platform,
        long? keyId,
        string model) => new()
    {
        BaseUrl = baseUrl,
        Platform = platform,
        RoutingMode = RoutingMode.Economy,
        KeySelectionInitialized = true,
        SelectedKeyIds = [10],
        ActiveProbeEnabled = true,
        ActiveProbeKeyId = keyId,
        ActiveProbeModel = model,
        ActiveProbeIntervalSeconds = 90
    };

    private static PreflightRoutingClient CreateSingleTargetAccount(
        DateTimeOffset now,
        Action<long, long, CancellationToken> afterUpdate) =>
        new(
            [Provider(2, 0.01, true, 1, now)],
            [Group(1), Group(2)],
            [
                new ApiKeyInfo { Id = 10, Name = "Business Key", Status = "active", GroupId = 1 },
                new ApiKeyInfo { Id = 99, Name = "Health Key", Status = "active", GroupId = 1 }
            ])
        {
            AfterUpdate = afterUpdate
        };
}

internal sealed class PreflightRoutingClient(
    IReadOnlyList<ProviderStatus> providers,
    IReadOnlyList<GroupInfo> groups,
    IEnumerable<ApiKeyInfo> keys) : IAIHubApiClient
{
    private readonly List<ApiKeyInfo> _keys = keys.ToList();
    private int _getKeysCalls;

    public Action<long, long, CancellationToken>? AfterUpdate { get; init; }
    public Func<int, Exception?>? GetKeysFailure { get; set; }
    public Func<long, long, ApiKeyInfo, ApiKeyInfo?>? UpdateResult { get; set; }

    public Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitorSummary { Apis = providers.ToList() });

    public Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(groups);

    public Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, double>>(new Dictionary<long, double>());

    public Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default)
    {
        var failure = GetKeysFailure?.Invoke(++_getKeysCalls);
        if (failure is not null)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyList<ApiKeyInfo>>(_keys.ToArray());
    }

    public Task<ApiKeyInfo> UpdateKeyGroupAsync(
        long keyId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        AfterUpdate?.Invoke(keyId, groupId, cancellationToken);
        var current = _keys.Single(key => key.Id == keyId);
        var updated = new ApiKeyInfo
        {
            Id = current.Id,
            Name = current.Name,
            Status = current.Status,
            GroupId = groupId,
            Group = groups.FirstOrDefault(group => group.Id == groupId)
        };
        _keys[_keys.IndexOf(current)] = updated;
        if (UpdateResult is { } updateResult)
        {
            return Task.FromResult(updateResult(keyId, groupId, updated)!);
        }

        return Task.FromResult(updated);
    }

    public void Dispose()
    {
    }
}

internal sealed class TrackingUpstreamProbeClient(
    Func<ActiveProbeRequest, CancellationToken, Task<ActiveProbeMeasurement>> responder,
    Action? onDispose = null) : IUpstreamProbeClient
{
    public List<ActiveProbeRequest> Requests { get; } = [];
    public bool Disposed { get; private set; }

    public Task<ActiveProbeMeasurement> ProbeAsync(
        ActiveProbeRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return responder(request, cancellationToken);
    }

    public void Dispose()
    {
        Disposed = true;
        onDispose?.Invoke();
    }
}

internal sealed class RecordingRouteStateStore : IRouteStateStore
{
    private RouteState _state;

    public RecordingRouteStateStore(RouteState? initialState = null)
    {
        _state = initialState ?? new RouteState();
    }

    public int SaveCalls { get; private set; }

    public RouteState Load() => _state;

    public void Save(RouteState state)
    {
        SaveCalls++;
        _state = state;
    }
}
