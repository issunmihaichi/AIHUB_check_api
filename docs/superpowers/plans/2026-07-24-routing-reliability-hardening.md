# Routing Reliability Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the Balanced countdown and make routing decisions use debounced policy changes, target health preflight, robust HTTP-200 JSON classification, 30-minute metrics, stale-evidence decay, and six-hour reliability scoring.

**Architecture:** Keep the WinForms shell thin and put decision behavior in Core. `RouteDecisionCoordinator` always sends Balanced mode through `BalancedDeadlineEngine`; `RouteDecisionEngine` applies the existing hysteresis to normal Deadline switches. `ProviderMetricsRollingWindow` owns 30-minute aggregation and local probe observations, while `RoutingEngine` owns eligibility/evidence weighting. `RoutingService` orchestrates target preflight before any business-Key write, and `AIHubClient` uses a focused response-envelope parser.

**Tech Stack:** .NET 10, C#, WinForms, `System.Text.Json`, deterministic console-based Core tests.

---

### Task 1: Remove Countdown State and Keep Explicit Task Size

**Files:**
- Modify: `src/AIHubRouter.Core/AppSettingsStore.cs`
- Modify: `src/AIHubRouter.Core/RoutingUiSettings.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Settings.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.SettingsDialog.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Routing.cs`
- Modify: `src/AIHubRouter.WinForms/RoutingSettingsDialog.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.SettingsDialog.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing settings and migration tests**

Add a `DurationCategory` field to the desired `RoutingUiSettings` contract in tests and remove assertions for `BalancedCountdownSeconds`/`BalancedCountdownEndsAtUtc`. Add a legacy JSON test that includes the old fields and asserts loading succeeds while `DurationCategory` is retained:

```csharp
var legacy = """
    {"durationCategory":2,"balancedCountdownSeconds":7200,
     "balancedCountdownEndsAtUtc":"2026-07-24T00:00:00Z"}
    """;
File.WriteAllText(Path.Combine(directory, "settings.json"), legacy);
var loaded = new AppSettingsStore(directory).Load().Settings;
Assert(loaded.DurationCategory == TaskDurationCategory.Long,
    "Legacy countdown settings did not preserve task size.");
```

- [ ] **Step 2: Run the Core tests and verify RED**

Run:

```powershell
dotnet run --project .\tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj --no-restore -c Release
```

Expected: compilation fails because `RoutingUiSettings.DurationCategory` does not yet exist and old countdown assertions no longer match production.

- [ ] **Step 3: Remove countdown production state**

Remove `BalancedCountdownSeconds` and `BalancedCountdownEndsAtUtc` from persistent/UI settings and MainForm fields. Remove `_balancedCountdownTimer`, countdown controls, reset handlers, `GetBalancedRemainingSeconds`, `RestartBalancedCountdown`, and `UpdateBalancedCountdownDisplay`. Keep `PersistentAppSettings.DurationCategory`.

Add the explicit setting:

```csharp
public TaskDurationCategory DurationCategory { get; init; } = TaskDurationCategory.Medium;
```

Normalize undefined enum values to `Medium`, and include the value in equality/hash calculations. Replace the countdown rows in the native settings dialog with a drop-down containing `短任务`, `1-4 小时`, and `4 小时以上`. `CurrentDurationCategory()` must return the stored selection rather than deriving it from seconds.

- [ ] **Step 4: Run Core tests and build WinForms**

Run:

```powershell
dotnet run --project .\tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj --no-restore -c Release
dotnet build .\src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj --no-restore -c Release
```

Expected: all tests pass and WinForms builds with no countdown-symbol references.

- [ ] **Step 5: Commit the task**

```powershell
git add src/AIHubRouter.Core/AppSettingsStore.cs src/AIHubRouter.Core/RoutingUiSettings.cs src/AIHubRouter.WinForms tests/AIHubRouter.Core.Tests
git commit -m "refactor: remove balanced countdown state"
```

### Task 2: Make Balanced Always Use Deadline and Apply Hysteresis

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionCoordinator.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Modify: `src/AIHubRouter.Core/RoutingService.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.BalancedDeadline.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing Balanced behavior tests**

Replace the zero-countdown Economy test with a test that omits countdown state and asserts Balanced still produces a `BalancedDeadlineDecision`. Add a hysteresis sequence whose state has a recent policy switch and whose current route misses the deadline:

```csharp
Assert(first.Decision.Reason == RouteDecisionReason.PolicySwitchCoolingDown,
    "Balanced Deadline bypassed the dwell guard.");
Assert(awaiting.Decision.Reason == RouteDecisionReason.PolicySwitchAwaitingEvaluations,
    "Balanced Deadline bypassed the completed-evaluation guard.");
Assert(stable.Decision.Reason == RouteDecisionReason.PolicyCandidateNotStable,
    "Balanced Deadline bypassed stable-target observations.");
Assert(accepted.Decision.ShouldSwitch &&
       accepted.Decision.Reason == RouteDecisionReason.BalancedDeadlineSwitched,
    "A stable Balanced target was not eventually accepted.");
```

Preserve tests proving initial routing and an invalid current route recover immediately.

- [ ] **Step 2: Run tests and verify RED**

Expected: the no-countdown case enters adaptive routing instead of Deadline, and Deadline switches bypass the hysteresis assertions.

- [ ] **Step 3: Implement the Balanced path**

Remove `BalancedRemainingSeconds` from `AdaptiveRoutingContext` and coordinator/service parameters. Resolve effective preference as:

```csharp
var effectivePreference = basePolicy.Mode switch
{
    RoutingMode.Economy => AdaptivePreference.Cost,
    RoutingMode.Balanced => AdaptivePreference.Balanced,
    _ => AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(currentInterval, basePreference)
};
```

Enter `BalancedDeadlineEngine` whenever `context.BaseMode == RoutingMode.Balanced`. Remove `DecideBalancedCountdownExpired`. In `ApplyBalancedDeadlineDecision`, pass a normal switch with a non-null current route through `ApplyPolicyHysteresis`; keep initial/forced recovery immediate.

- [ ] **Step 4: Run the full Core suite**

Expected: all Balanced, adaptive, forced-recovery, and hysteresis tests pass.

- [ ] **Step 5: Commit the task**

```powershell
git add src/AIHubRouter.Core tests/AIHubRouter.Core.Tests
git commit -m "fix: debounce balanced deadline switches"
```

### Task 3: Add 30-Minute Metrics, Stale Decay, and Reliability Score

**Files:**
- Modify: `src/AIHubRouter.Core/ProviderMetricsRollingWindow.cs`
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/AppSettingsStore.cs`
- Modify: `src/AIHubRouter.Core/RoutingEngine.cs`
- Modify: `src/AIHubRouter.Core/ProviderStatusPresentation.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Routing.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.ProviderMetrics.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.Routing.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestFixtures.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing 30-minute and latest-state tests**

Assert `DefaultWindow == 30 minutes`, a sample at exactly the cutoff is retained, and a sample older by one tick is removed. Change the aggregation test so a latest `Available=false`/`Enabled=false` wins over historical true samples and a later true state can recover.

- [ ] **Step 2: Write failing stale-weight/ranking tests**

Expose a deterministic evidence-weight API and assert:

```csharp
Assert(RoutingEngine.CalculateEvidenceWeight(now.AddMinutes(-30), now, age) == 1, "30m boundary changed.");
Assert(Math.Abs(RoutingEngine.CalculateEvidenceWeight(now.AddHours(-1), now, age) - 0.5) < 1e-12, "60m decay changed.");
Assert(RoutingEngine.CalculateEvidenceWeight(now.AddHours(-8), now, age) == 0.25, "Stale floor changed.");
```

Add ranking cases proving stale candidates remain eligible, positive stale speed benefit is reduced, and price penalty is unchanged. Add a pair differing only in `SuccessRate6h` and assert the higher-reliability candidate receives the higher score.

- [ ] **Step 3: Run tests and verify RED**

Expected: the window remains 20 minutes, booleans use historical medians, stale evidence is not weighted, and reliability only tie-breaks.

- [ ] **Step 4: Implement metrics and scoring**

Set the window and policy age to 30 minutes. Aggregate numeric metrics with the rolling median, but copy latest `Available`, `Enabled`, and `CheckedAt`. Add `EvidenceWeight` to `RouteCandidate` and calculate:

```csharp
weight = age <= maximumAge ? 1 : Math.Max(0.25, maximumAge.TotalSeconds / age.TotalSeconds);
reliabilityDelta = candidateSuccess6h - baselineSuccess6h;
score = latencyWeight * weightedSpeedup
      - priceWeight * pricePremiumRatio
      + 0.15 * weightedPositiveReliabilityDelta;
```

Only positive speed/reliability improvements are multiplied by stale evidence weight. Replace the hard age filter with “has a usable timestamp or performance observation”; continue to hard-filter latest unavailable/disabled data. Make presentation show `数据陈旧（已降权）` while agreeing with routing eligibility.

- [ ] **Step 5: Run tests and commit**

Expected: full Core suite passes, including fresh/stale presentation parity.

```powershell
git add src/AIHubRouter.Core src/AIHubRouter.WinForms/MainForm.Routing.cs tests/AIHubRouter.Core.Tests
git commit -m "feat: rank thirty-minute and stale provider evidence"
```

### Task 4: Classify HTTP-200 Business and Format Errors

**Files:**
- Create: `src/AIHubRouter.Core/ApiResponseEnvelope.cs`
- Modify: `src/AIHubRouter.Core/AIHubClient.cs`
- Modify: `src/AIHubRouter.Core/SafeErrorPresentation.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.PersistenceAndAuth.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing envelope compatibility tests**

Exercise public client methods with HTTP-200 bodies for `code:200`, `code:"OK"`, `success:true`, `status:"success"`, `data`, `result`, direct `MonitorSummary`, and direct objects containing `status:"active"`. Assert each valid payload deserializes.

- [ ] **Step 2: Write failing error-priority tests**

Assert `success:false`, non-empty `error`, `code:"UPSTREAM_FAILED"`, conflicting `{code:0,success:false,...}`, malformed JSON, empty body, and a success wrapper without payload throw `AIHubApiException`. Assert nested `error.code` is captured and `SafeErrorPresentation.GetMessage` does not contain `HTTP 200`.

- [ ] **Step 3: Run tests and verify RED**

Expected: compatibility envelopes either fail deserialization or are misclassified, and safe presentation reports HTTP 200.

- [ ] **Step 4: Implement a focused envelope parser**

Create an internal parser that treats explicit error signals first; accepts scalar success values `0`, `200-299`, `OK`, and `SUCCESS`; unwraps `data`, `result`, or `payload`; and distinguishes direct business objects from envelopes. `status` is envelope metadata only when another envelope field is present, so `GroupInfo.status="active"` remains direct data. Clone/deserialize `JsonElement` before disposing its document.

For a 2xx `AIHubApiException`, return a safe business/format message instead of `HTTP 200`.

- [ ] **Step 5: Run Core tests and commit**

```powershell
git add src/AIHubRouter.Core/ApiResponseEnvelope.cs src/AIHubRouter.Core/AIHubClient.cs src/AIHubRouter.Core/SafeErrorPresentation.cs tests/AIHubRouter.Core.Tests
git commit -m "fix: classify http 200 api error envelopes"
```

### Task 5: Persist Active Probe Success and Failure as Routing Evidence

**Files:**
- Modify: `src/AIHubRouter.Core/ActiveProviderProbe.cs`
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/ProviderMetricsRollingWindow.cs`
- Modify: `src/AIHubRouter.Core/RoutingEngine.cs`
- Modify: `src/AIHubRouter.Core/ProviderStatusPresentation.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.ActiveProbe.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.ActiveProbe.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.Routing.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing probe-observation tests**

Define the desired observation behavior in tests: fresh success supplies local TTFT and freshness, fresh failure removes the group from eligibility, a later success recovers it, and an expired failure is neutral. Verify presentation and routing agree.

- [ ] **Step 2: Run tests and verify RED**

Expected: `RecordActiveProbes` cannot record failures and `RoutingEngine` ignores probe health.

- [ ] **Step 3: Implement probe observations**

Add a validated `ActiveProbeObservation` containing platform, group, time, success, and optional latency. Store observations by platform/group in the rolling window. Aggregate successful latency while exposing latest nullable `ActiveProbeHealthy`, `ActiveProbeCheckedAt`, and sample count on `ProviderStatus`.

Add `ActiveProbeMaximumAge` to routing policy. A fresh `ActiveProbeHealthy=false` is a hard eligibility failure; fresh success is usable freshness evidence; expired state is neutral. Update the existing scheduled selected-Key check to record both outcomes and recalculate the candidate.

- [ ] **Step 4: Make streaming HTTP-200 JSON errors explicit**

When a nominal streaming response has an `application/json` error body or an SSE `data:` error object, classify it as a failed probe with a safe code/detail. Preserve normal first-content-token behavior and never treat malformed data as success.

- [ ] **Step 5: Run Core tests and commit**

```powershell
git add src/AIHubRouter.Core src/AIHubRouter.WinForms/MainForm.ActiveProbe.cs tests/AIHubRouter.Core.Tests
git commit -m "feat: use active probe health in route eligibility"
```

### Task 6: Preflight the Proposed Target Before Business-Key Writes

**Files:**
- Modify: `src/AIHubRouter.Core/ActiveProviderProbe.cs`
- Modify: `src/AIHubRouter.Core/RoutingService.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.ActiveProbe.cs`
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.Routing.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestFixtures.cs`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing target-preflight integration tests**

Add a test routing client with a dedicated probe Key and an injected probe client. Cover: successful target permits business-Key writes; failed target is excluded and the next candidate is selected; all failed targets produce no business-Key writes; restoration failure aborts; same-group probe avoids an unnecessary PUT; dry-run never probes or moves a Key.

- [ ] **Step 2: Run tests and verify RED**

Expected: `RoutingService` writes the proposed target without invoking an upstream probe.

- [ ] **Step 3: Add single-target probe with guaranteed restore**

Extract/reuse the existing `remoteGroupMayHaveChanged` restoration pattern in `ActiveProviderProbeService`. The new method probes one requested group, restores using `CancellationToken.None`, converts ordinary upstream failure into an observation, propagates caller cancellation, and throws `ActiveProbeRestoreException` when restoration is uncertain.

- [ ] **Step 4: Add the bounded routing evaluation loop**

Inject an upstream-probe factory into `RoutingService`. For a real cycle with valid probe settings, consume a fresh cached result or probe the selected target. On failure, add the group to a per-cycle exclusion set, merge it with the configured blocklist, and re-run `RouteDecisionCoordinator`. Limit attempts to the number of eligible groups. Only after a healthy target is resolved may business Keys be updated. Dry-run uses cached observations only.

- [ ] **Step 5: Run Core tests and commit**

```powershell
git add src/AIHubRouter.Core tests/AIHubRouter.Core.Tests
git commit -m "feat: preflight routed targets with health key"
```

### Task 7: Documentation, Simulation, and Release Build Verification

**Files:**
- Modify: `docs/ALGORITHM_MODES.md`
- Modify: `docs/ROUTING_STABILITY_V2.md`
- Modify: `README.md`

- [ ] **Step 1: Update the algorithm documentation**

Remove countdown/expiry descriptions. Document task size as independent, Balanced hysteresis, target preflight, HTTP-200 business errors, the 30-minute median, stale decay formula, latest-state hard gates, active-probe TTL, and the `0.15` six-hour reliability term.

- [ ] **Step 2: Run deterministic route simulations**

Use the Core tests to cover at least: fresh cheap healthy, stale cheap healthy, stale apparently fast versus fresh moderate, provider-reported healthy but probe-failed, Balanced deadline oscillation, forced unavailable recovery, and malformed HTTP-200 payload.

- [ ] **Step 3: Run full verification**

```powershell
dotnet run --project .\tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj --no-restore -c Release
dotnet build .\src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj --no-restore -c Release
git diff --check
rg -n "BalancedCountdown|倒计时|20-minute|20 分钟|HTTP 200 请求失败" src tests docs README.md
```

Expected: all tests pass, Release build has zero errors/warnings, diff check is clean, and obsolete behavior references are absent except intentional legacy-migration fixtures/audit enum compatibility.

- [ ] **Step 4: Perform final credential scan and commit**

Run the repository publish/source scanner or equivalent existing release-safety test, inspect the staged diff, and commit only source, tests, and documentation.

```powershell
git add README.md docs src tests
git commit -m "docs: describe hardened routing decisions"
```
