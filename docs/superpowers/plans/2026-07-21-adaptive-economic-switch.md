# Adaptive Economic Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the supplied token-cost, context-penalty, completion-time, duration, and call-interval rules to native WinForms route changes.

**Architecture:** A pure Core decision component owns constants, duration configuration, preference overrides, formulas, and pairwise decisions. `RoutingService` resolves the latest call interval for the current Key group, evaluates candidates with the effective mode, then `RouteDecisionEngine` maps the pure result into existing Key updates, UI state, and credential-free audit records.

**Tech Stack:** C# 14, .NET 10, Windows Forms, `System.Text.Json`, executable Core test harness, PowerShell release gates.

---

## File Map

- Create `src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs`: constants, duration configuration, interval resolution, formulas, and pure decision result.
- Modify `src/AIHubRouter.Core/Models.cs`: last-call JSON fields, duration/preference/reason types, and safe decision metrics.
- Modify `src/AIHubRouter.Core/AppSettingsStore.cs`: persist Medium/Short/Long selection.
- Modify `src/AIHubRouter.Core/RouteDecisionEngine.cs`: replace score threshold with adaptive pairwise guard.
- Modify `src/AIHubRouter.Core/RoutingService.cs`: derive interval/effective mode before evaluation.
- Modify `src/AIHubRouter.Core/AuditLogWriter.cs`: record local numeric decision facts without credentials or request content.
- Modify `src/AIHubRouter.WinForms/MainForm.Layout.cs`: add compact native duration combo.
- Modify `src/AIHubRouter.WinForms/MainForm.cs`: save/load duration and show effective preference/reason.
- Modify `tests/AIHubRouter.Core.Tests/Program.cs`: pure algorithm, integration, persistence, audit, and regression tests.
- Modify `README.md`: document dynamic behavior, required provider field, and duration semantics.

### Task 1: Last-Call Contract, Constants, And Preference Resolution

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Create: `src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Register contract, constant, and interval-boundary tests**

Add registrations for:

```csharp
("Provider last-call aliases deserialize", TestProviderLastCallAliasesDeserialize),
("Adaptive constants match supplied economics", TestAdaptiveConstants),
("Adaptive preference follows interval boundaries", TestAdaptivePreferenceBoundaries),
("Current-group interval uses latest provider call", TestCurrentGroupIntervalResolution),
("Missing call time retains base preference", TestMissingCallTimeRetainsBasePreference),
```

Use JSON containing `lastCallEndedAt` and `lastCallAt` in separate providers and
assert `ResolvedLastCallEndedAt` prefers the first field. Assert the exact
constant/configuration values from the design. Assert preference results at
`4.999`, `5`, `15`, `15.001`, `30`, and `30.001` seconds for every relevant
base mode. For interval resolution, include two current-group providers and a
newer unrelated group; assert the current group wins.

- [ ] **Step 2: Run the Core tests and verify RED**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
```

Expected: compilation fails because the last-call fields,
`AdaptiveRoutingConstants`, `AdaptivePreference`, `TaskDurationCategory`, and
resolver APIs do not exist.

- [ ] **Step 3: Add model types and explicit JSON aliases**

Add to `ProviderStatus`:

```csharp
[JsonPropertyName("lastCallEndedAt")]
public DateTimeOffset? LastCallEndedAt { get; init; }

[JsonPropertyName("lastCallAt")]
public DateTimeOffset? LastCallAt { get; init; }

public DateTimeOffset? ResolvedLastCallEndedAt => LastCallEndedAt ?? LastCallAt;
```

Add:

```csharp
public enum TaskDurationCategory { Short, Medium, Long }
public enum AdaptivePreference { Cost, Balanced, Speed }

public sealed record DurationConfiguration(
    double MinimumRemainingTokens,
    double MaximumRemainingTokens,
    double ExpectedCompletionSeconds);
```

- [ ] **Step 4: Add the global constants and resolvers**

Create `AdaptiveSwitchDecisionEngine.cs` with:

```csharp
public static class AdaptiveRoutingConstants
{
    public const double InputPricePerMillion = 5.0;
    public const double OutputPricePerMillion = 30.0;
    public const double PenaltyTokens = 300_000;
    public const double PlanningTokensPerSecond = 43.6;
    public const double MinimumUsefulRemainingTokens = 1_000;
    public const double MaximumCostCompletionSeconds = 24 * 60 * 60;

    public static DurationConfiguration Duration(TaskDurationCategory category) => category switch
    {
        TaskDurationCategory.Short => new(0, 156_960, 3_600),
        TaskDurationCategory.Medium => new(156_960, 627_840, 7_200),
        TaskDurationCategory.Long => new(627_840, 3_767_040, 21_600),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
```

Add pure methods:

```csharp
public static AdaptivePreference ToPreference(RoutingMode mode);
public static RoutingMode ToRoutingMode(AdaptivePreference preference);
public static AdaptivePreference ResolveEffectivePreference(
    double? currentIntervalSeconds,
    AdaptivePreference basePreference);
public static double? ResolveCurrentIntervalSeconds(
    IEnumerable<ProviderStatus> providers,
    long? currentGroupId,
    string platform,
    DateTimeOffset now);
```

Use exact inclusive boundaries from the supplied pseudocode. Filter interval
timestamps by platform, prefer the current group when available, take the
latest resolved timestamp, clamp future skew up to one minute to zero, and
return `null` for missing or more-distant future values.

- [ ] **Step 5: Verify GREEN and commit**

Run the Core tests, then:

```powershell
git add src/AIHubRouter.Core/Models.cs `
        src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs `
        tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: add adaptive routing inputs and constants"
```

### Task 2: Pure Economic Switch Decisions

**Files:**
- Modify: `src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs`
- Modify: `src/AIHubRouter.Core/Models.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Register formula and branch tests**

Register focused tests for:

```csharp
("Adaptive penalty uses new multiplier", TestAdaptivePenalty),
("Adaptive completion time includes TTFT", TestAdaptiveCompletionTime),
("Adaptive net saving subtracts context penalty", TestAdaptiveNetSaving),
("Adaptive cost accepts positive saving", TestAdaptiveCostAcceptsPositiveSaving),
("Adaptive cost rejects slow candidate", TestAdaptiveCostRejectsSlowCandidate),
("Adaptive balanced requires all safeguards", TestAdaptiveBalancedSafeguards),
("Adaptive speed accepts generation boost", TestAdaptiveSpeedAcceptsGenerationBoost),
("Adaptive speed accepts end-to-end gain", TestAdaptiveSpeedAcceptsEndToEndGain),
("Adaptive short task is protected outside cost", TestAdaptiveShortTaskProtection),
("Adaptive invalid performance cannot switch", TestAdaptiveInvalidPerformance),
```

Each test constructs a real `AdaptiveSwitchRequest`, calls `Decide`, and
asserts the decision, stable reason, effective preference, and numeric metrics.
Use exact boundary cases for 5 percent price reduction, 120 percent generation
speed, 110 percent price, 30 seconds end-to-end gain, and 24 hours completion.

- [ ] **Step 2: Run tests and verify RED**

Run the Core test command. Expected: compilation fails for the request/result,
calculation methods, and adaptive reason enum.

- [ ] **Step 3: Define the pure request/result contract**

Add:

```csharp
public sealed record AdaptiveSwitchRequest(
    double OldMultiplier,
    double NewMultiplier,
    double OldTtftSeconds,
    double NewTtftSeconds,
    double OldGenerationSpeed,
    double NewGenerationSpeed,
    TaskDurationCategory DurationCategory,
    AdaptivePreference BasePreference,
    double? CurrentIntervalSeconds);

public enum AdaptiveDecisionReason
{
    AcceptedCost,
    AcceptedBalanced,
    AcceptedSpeed,
    NewPriceNotLower,
    ShortTaskProtected,
    RemainingWorkTooSmall,
    CostGuardRejected,
    BalancedGuardRejected,
    SpeedGuardRejected
}

public sealed record AdaptiveSwitchDecision(
    bool ShouldSwitch,
    AdaptiveDecisionReason Reason,
    AdaptivePreference EffectivePreference,
    double RemainingTokens,
    double PenaltyUsd,
    double NetSavingUsd,
    double OldCompletionSeconds,
    double NewCompletionSeconds,
    double DeltaSeconds,
    string Detail);
```

- [ ] **Step 4: Implement the supplied formulas and branches exactly**

Expose testable methods:

```csharp
public static double CalculatePenalty(double newMultiplier) =>
    AdaptiveRoutingConstants.PenaltyTokens * newMultiplier *
    AdaptiveRoutingConstants.InputPricePerMillion / 1_000_000;

public static double CalculateCompletionTime(
    double ttftSeconds,
    double generationSpeed,
    double remainingTokens);

public static double CalculateNetSaving(
    double oldMultiplier,
    double newMultiplier,
    double remainingTokens);

public static AdaptiveSwitchDecision Decide(AdaptiveSwitchRequest request);
```

Select `R_max` only for Cost and `R_min` otherwise. Apply the two early rejects,
the `R <= 1000` guard, then the Cost/Balanced/Speed conditions in their supplied
order. Use invariant, bounded local detail strings such as
`净省 $0.1234，时间增加 12.3 秒`; never include provider messages or request
content.

- [ ] **Step 5: Verify GREEN and commit**

Run the Core tests, then:

```powershell
git add src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs `
        src/AIHubRouter.Core/Models.cs `
        tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: implement economic switch safeguards"
```

### Task 3: Route Evaluation And Key Update Integration

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Modify: `src/AIHubRouter.Core/RoutingService.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Replace score-threshold tests with adaptive integration tests**

Remove registrations that assert `ScoreAdvantageTooSmall`. Add tests proving:

```csharp
("Cost mode proposes strict cheapest candidate", TestCostModeProposesCheapest),
("Frequent calls override economy with speed", TestFrequentCallsOverrideEconomy),
("Idle calls override speed with cost", TestIdleCallsOverrideSpeed),
("Adaptive rejection keeps current group", TestAdaptiveRejectionKeepsCurrentGroup),
("Adaptive acceptance updates selected Keys", TestAdaptiveAcceptanceUpdatesKeys),
("Initial and invalid routes recover immediately", TestAdaptiveRecoveryBypassesGuard),
```

Providers in these tests must include finite TTFT and output generation speed.
Service tests set `lastCallEndedAt` relative to the injected clock and assert
the effective preference and optional interval exposed by the decision.

- [ ] **Step 2: Run tests and verify RED**

Expected: old score-threshold behavior or missing adaptive context/metrics
causes the new assertions to fail.

- [ ] **Step 3: Extend route decisions without breaking constructors**

Keep the existing positional `RouteDecision` arguments and add init-only
properties:

```csharp
public AdaptivePreference? EffectivePreference { get; init; }
public TaskDurationCategory? DurationCategory { get; init; }
public double? CurrentIntervalSeconds { get; init; }
public AdaptiveSwitchDecision? AdaptiveDecision { get; init; }
public string Detail { get; init; } = string.Empty;
```

Replace `ScoreAdvantageTooSmall` with stable adaptive route reasons or map
`AdaptiveDecisionReason` through a dedicated switch. Retain older enum members
only if required to deserialize existing audit logs; do not generate them.

- [ ] **Step 4: Apply the adaptive guard in `RouteDecisionEngine`**

Change `Decide` to accept:

```csharp
public sealed record AdaptiveRoutingContext(
    RoutingMode BaseMode,
    TaskDurationCategory DurationCategory,
    double? CurrentIntervalSeconds);
```

Resolve Cost targets from `evaluation.Baseline`; resolve Balanced/Speed targets
from `evaluation.Recommended`. Preserve no-candidate, initial, invalid-current,
and already-optimal branches. For a real current/target pair, convert TTFT from
milliseconds to seconds and pass output TPS into `AdaptiveSwitchDecisionEngine`.
An accepted decision switches to the proposed target. A rejected decision
targets the current group, preserves state, and exposes the adaptive reason,
metrics, and local detail.

- [ ] **Step 5: Resolve interval and effective mode in `RoutingService`**

Before evaluating:

```csharp
var state = _stateStore.Load();
var currentGroupId = observedGroupId ?? state.CurrentGroupId;
var basePolicy = _settings.CreatePolicy();
var currentInterval = AdaptiveSwitchDecisionEngine.ResolveCurrentIntervalSeconds(
    summary.Apis, currentGroupId, basePolicy.Platform, now);
var basePreference = AdaptiveSwitchDecisionEngine.ToPreference(basePolicy.Mode);
var effectivePreference = AdaptiveSwitchDecisionEngine.ResolveEffectivePreference(
    currentInterval, basePreference);
var effectivePolicy = basePolicy with
{
    Mode = AdaptiveSwitchDecisionEngine.ToRoutingMode(effectivePreference)
};
```

Evaluate with `effectivePolicy`, then decide with `state` and an
`AdaptiveRoutingContext` built from the base mode, persisted duration, and
interval. Do not change dry-run, retry, partial-failure, mixed-Key, or state
persistence behavior.

- [ ] **Step 6: Verify GREEN and commit**

Run all Core tests, then:

```powershell
git add src/AIHubRouter.Core/Models.cs `
        src/AIHubRouter.Core/RouteDecisionEngine.cs `
        src/AIHubRouter.Core/RoutingService.cs `
        tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: apply adaptive decisions to Key routing"
```

### Task 4: Persistence, Native WinForms Controls, And Audit

**Files:**
- Modify: `src/AIHubRouter.Core/AppSettingsStore.cs`
- Modify: `src/AIHubRouter.Core/AuditLogWriter.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write persistence and audit tests**

Extend settings tests to assert Medium by default and a Short/Long selection
roundtrips through JSON. Extend the audit test to assert `effectivePreference`,
`durationCategory`, `currentIntervalSeconds`, `penaltyUsd`, `netSavingUsd`, and
completion-time fields are valid JSON while the existing password/refresh/
token/cookie/User-Agent checks remain unchanged.

- [ ] **Step 2: Run tests and verify RED**

Expected: missing settings and audit properties fail the new assertions.

- [ ] **Step 3: Persist the duration selection**

Add to `PersistentAppSettings`:

```csharp
public TaskDurationCategory DurationCategory { get; init; } = TaskDurationCategory.Medium;
```

Do not encrypt it because it is a non-sensitive routing preference.

- [ ] **Step 4: Extend safe audit fields**

Add optional locally generated fields to `RouteAuditEntry` for effective
preference, duration, interval, penalty, net saving, old/new completion, delta,
and adaptive reason. Normalize every double with the existing finite-value
pattern; serialize non-finite completion values as `null`. Do not record
remaining-token field names because the existing audit contract intentionally
rejects any `token` property name.

- [ ] **Step 5: Add and wire the native duration combo**

In `MainForm.Layout.cs` add:

```csharp
private readonly ComboBox _durationCombo = new()
{
    DropDownStyle = ComboBoxStyle.DropDownList,
    Width = 110
};
```

Populate `短任务`, `1-4 小时`, and `4 小时以上`; add label `时长` next to the
existing mode control in the wrapping toolbar. Add a tooltip explaining only
the three duration ranges, without in-app algorithm narration.

In `MainForm.cs`, map the combo to `TaskDurationCategory`, save/load it, and on
selection changes invalidate the service, recalculate, and save. Use
`Decision.Detail` and effective preference in the status strip. Populate the
new safe audit fields from `Decision.AdaptiveDecision`.

- [ ] **Step 6: Verify tests and Release build**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
dotnet build AIHubRouter.sln --no-restore -c Release
```

Expected: all tests pass; build reports 0 warnings and 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add src/AIHubRouter.Core/AppSettingsStore.cs `
        src/AIHubRouter.Core/AuditLogWriter.cs `
        src/AIHubRouter.WinForms/MainForm.Layout.cs `
        src/AIHubRouter.WinForms/MainForm.cs `
        tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: expose adaptive routing duration in WinForms"
```

### Task 5: Documentation, Release Gates, And PR Update

**Files:**
- Modify: `README.md`
- Verify: `scripts/publish.ps1`
- Verify: `AIHubRouter.sln`

- [ ] **Step 1: Document the new algorithm inputs and fallback**

Document constants, duration categories, interval overrides, the accepted
`lastCallEndedAt`/`lastCallAt` provider fields, and the missing-field fallback
to base preference. State that the native app reacts on the next manual or
automatic provider refresh and remains Windows WinForms-only.

- [ ] **Step 2: Run complete verification**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
dotnet build AIHubRouter.sln --no-restore -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

Expected: all Core tests pass; Release has 0 warnings/errors; source, DLL, and
both EXEs scan clean; portable and lite folders each contain only
`AIHubRouter.exe`.

- [ ] **Step 3: Confirm forbidden platforms remain absent**

Run:

```powershell
$forbidden = rg --files | Select-String 'AIHubRouter\.(Cli|Desktop)|Avalonia|Skia|systemd|\.service$|linux|osx|arm64'
if ($forbidden) { $forbidden; exit 1 }
```

Expected: no output, exit code 0.

- [ ] **Step 4: Request independent review**

Review the new range from `1243ec8` through HEAD. Fix every confirmed Critical
or Important finding with a failing test first. Re-run the full gates after
any fix.

- [ ] **Step 5: Commit docs and update PR #2**

```powershell
git add README.md
git commit -m "docs: explain adaptive economic routing"
git diff --check
git status --short
git push origin codex/sync-onrightpath-v1.0.3
gh pr view 2 --json url,state,headRefName,baseRefName
```

## Self-Review Record

- Spec coverage: constants, `R_min/R_max`, interval boundaries, all three
  decision branches, provider last-call fields, duration UI/persistence,
  routing integration, local reason logging, and Win32-only delivery each map
  to a task.
- No placeholders: every task identifies concrete types, signatures, tests,
  commands, and expected results.
- Type consistency: `TaskDurationCategory`, `AdaptivePreference`,
  `AdaptiveSwitchRequest`, `AdaptiveSwitchDecision`, and
  `AdaptiveRoutingContext` are defined before integration use.
- Safety: missing call time uses base preference; invalid performance cannot
  cause a switch; initial/invalid routes still recover; audit contains no
  credentials, request content, raw server messages, or provider warnings.
