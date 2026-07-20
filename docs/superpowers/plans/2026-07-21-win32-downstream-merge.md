# AIHubRouter Win32 Downstream Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the useful weighted-routing, audit, caching, and adaptive-theme work from `OnRightPath/AIHubRouter` into the existing native Windows WinForms application without adding any cross-platform application or release surface.

**Architecture:** `AIHubRouter.Core` owns deterministic route evaluation, route decisions, authenticated routing cycles, non-sensitive state, and credential-free audit records. `AIHubRouter.WinForms` remains the only application and renders Core results with native controls and Windows theme detection. Existing DPAPI credential storage, refresh-first authentication, selected-Key persistence, rendering controls, and Windows release gates remain authoritative.

**Tech Stack:** C# 14, .NET 10, Windows Forms, `System.Text.Json`, Windows DPAPI, Windows Registry, the repository's executable Core test harness, PowerShell release scripts.

---

## File Map

- Modify `src/AIHubRouter.Core/Models.cs`: weighted-routing policy, evaluation, decision, state, and cycle-result records.
- Modify `src/AIHubRouter.Core/RoutingEngine.cs`: deterministic weighted evaluation while preserving `SelectCheapest`.
- Create `src/AIHubRouter.Core/RouteDecisionEngine.cs`: convert evaluation plus observed/persisted state into an explainable decision.
- Create `src/AIHubRouter.Core/RouteStateStore.cs`: Windows JSON route-state persistence with atomic replacement.
- Modify `src/AIHubRouter.Core/AIHubClient.cs`: implement a small API-client interface used by the service.
- Create `src/AIHubRouter.Core/RoutingService.cs`: one authenticated cycle, cache, dry-run, bounded auth retry, updates, and state transitions.
- Create `src/AIHubRouter.Core/AuditLogWriter.cs`: bounded rotating JSONL log containing only explicit non-secret fields.
- Modify `src/AIHubRouter.Core/AppSettingsStore.cs`: routing mode, account cache duration, and native theme preference defaults.
- Modify `tests/AIHubRouter.Core.Tests/Program.cs`: deterministic coverage for all Core behavior above.
- Modify `src/AIHubRouter.WinForms/GridRows.cs`: provider score and decision-state presentation fields.
- Create `src/AIHubRouter.WinForms/NativeThemeManager.cs`: system/light/dark WinForms palettes and Windows registry detection.
- Modify `src/AIHubRouter.WinForms/MainForm.Layout.cs`: mode selector, simulation command, theme selector, and score columns.
- Modify `src/AIHubRouter.WinForms/MainForm.cs`: use `RoutingService`, persist preferences, write audit records, and render decisions.
- Modify `README.md`: visible downstream feature attribution and Win32-only support statement.

### Task 1: Routing Domain And Persistent Preferences

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/AppSettingsStore.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing default and roundtrip tests**

Add executable test registrations and methods asserting:

```csharp
("Routing preferences default to Win32-compatible values", TestRoutingPreferenceDefaults),
("Routing preferences roundtrip", TestRoutingPreferenceRoundtrip),

static void TestRoutingPreferenceDefaults()
{
    var settings = new PersistentAppSettings();
    Assert(settings.RoutingMode == RoutingMode.Economy, "New installs must preserve lowest-price routing.");
    Assert(settings.AccountCacheSeconds == 300, "Account cache default changed.");
    Assert(settings.Theme == WinFormsTheme.System, "Theme must follow Windows by default.");
}

static void TestRoutingPreferenceRoundtrip()
{
    if (!OperatingSystem.IsWindows()) return;
    var directory = Path.Combine(Path.GetTempPath(), "AIHubRouter.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(directory);
        store.Save(new PersistentAppSettings
        {
            RoutingMode = RoutingMode.Speed,
            AccountCacheSeconds = 90,
            Theme = WinFormsTheme.Dark
        }, null);
        var loaded = store.Load().Settings;
        Assert(loaded.RoutingMode == RoutingMode.Speed, "Routing mode did not roundtrip.");
        Assert(loaded.AccountCacheSeconds == 90, "Cache duration did not roundtrip.");
        Assert(loaded.Theme == WinFormsTheme.Dark, "Theme did not roundtrip.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
```

- [ ] **Step 2: Run the Core tests and confirm RED**

Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release`

Expected: compile failure because `RoutingMode`, `WinFormsTheme`, and the new settings properties do not exist.

- [ ] **Step 3: Add the domain types and preference defaults**

Add these public contracts to `Models.cs`:

```csharp
public enum RoutingMode { Economy, Balanced, Speed }
public enum WinFormsTheme { System, Light, Dark }

public sealed record BalancedRoutingPolicy
{
    public string Platform { get; init; } = "openai";
    public RoutingMode Mode { get; init; } = RoutingMode.Economy;
    public double MinimumSuccessRate6h { get; init; } = 0.9;
    public TimeSpan MaximumStatusAge { get; init; } = TimeSpan.FromMinutes(15);
    public double PriceWeight => Mode switch
    {
        RoutingMode.Economy => 0.95,
        RoutingMode.Balanced => 0.80,
        RoutingMode.Speed => 0.35,
        _ => 0.95
    };
    public double LatencyWeight => 1 - PriceWeight;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Platform))
            throw new ArgumentException("Platform is required.", nameof(Platform));
        if (!double.IsFinite(MinimumSuccessRate6h) || MinimumSuccessRate6h is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSuccessRate6h));
        if (MaximumStatusAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumStatusAge));
        if (!double.IsFinite(PriceWeight) || PriceWeight is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(Mode));
    }
}

public sealed record RouteEvaluation(
    RouteCandidate? Recommended,
    RouteCandidate? Baseline,
    IReadOnlyList<RouteCandidate> EligibleCandidates,
    IReadOnlyDictionary<long, double> CandidateScores,
    double? MinimumMultiplier,
    double PriceWeight,
    double LatencyWeight);

public enum RouteDecisionReason
{
    NoCandidate, InitialRoute, CurrentRouteInvalid, AlreadyOptimal,
    BetterPrice, FasterForWeightedTradeoff
}

public sealed record RouteDecision(
    RouteCandidate? Current,
    RouteCandidate? Target,
    bool ShouldSwitch,
    RouteDecisionReason Reason,
    double PricePremiumPercent,
    double? LatencyImprovementPercent,
    DateTimeOffset EvaluatedAt);

public sealed record RouteState { public long? CurrentGroupId { get; init; } }
```

Add to `PersistentAppSettings`:

```csharp
public RoutingMode RoutingMode { get; init; } = RoutingMode.Economy;
public int AccountCacheSeconds { get; init; } = 300;
public WinFormsTheme Theme { get; init; } = WinFormsTheme.System;

public BalancedRoutingPolicy CreateRoutingPolicy() => new()
{
    Platform = Platform,
    Mode = RoutingMode,
    MinimumSuccessRate6h = MinimumSuccessPercent / 100d,
    MaximumStatusAge = TimeSpan.FromMinutes(15)
};
```

- [ ] **Step 4: Run tests and confirm GREEN**

Run the same Core test command. Expected: all existing tests plus the two new tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/Models.cs src/AIHubRouter.Core/AppSettingsStore.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: add Win32 routing preferences"
```

### Task 2: Weighted Evaluation

**Files:**
- Modify: `src/AIHubRouter.Core/RoutingEngine.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Add deterministic failing evaluation tests**

Port the downstream synthetic-provider scenarios and assert exact winners for:

```csharp
("Balanced mode buys meaningful latency", TestBalancedModeBuysLatency),
("Balanced mode keeps price for moderate speed gap", TestBalancedModeKeepsPriceForModerateGap),
("Economy mode protects price", TestEconomyModeProtectsPrice),
("Speed mode accepts larger price premium", TestSpeedModeAcceptsLargerPremium),
("Missing latency ranks last", TestMissingLatencyRanksLast),
("Invalid measurements are excluded", TestInvalidMeasurementsAreExcluded),
("Zero multiplier remains free", TestZeroMultiplierWindow),
```

Each test calls the wished-for API:

```csharp
var result = RoutingEngine.Evaluate(providers, groups, rates, new BalancedRoutingPolicy
{
    Platform = "openai",
    Mode = RoutingMode.Balanced,
    MinimumSuccessRate6h = 0,
    MaximumStatusAge = TimeSpan.FromMinutes(15)
}, now);
Assert(result.Recommended?.Group.Id == expectedGroupId, "Unexpected weighted recommendation.");
Assert(result.CandidateScores.ContainsKey(expectedGroupId), "Recommended score was not exposed.");
```

Use only synthetic IDs and values; cover null, zero, negative, `double.NaN`, and both infinities without network access.

- [ ] **Step 2: Verify RED**

Run the Core test command. Expected: compile failure because `RoutingEngine.Evaluate` is absent.

- [ ] **Step 3: Implement the minimal deterministic evaluator**

Keep the existing hard filters. Deduplicate provider observations by group using valid first-token latency, then success rate and multiplier. Score measured candidates using:

```csharp
var pricePremiumRatio = (candidate.EffectiveMultiplier - minimumMultiplier) / minimumMultiplier;
var speedupRatio = baselineLatency / candidate.Provider.FirstTokenLatencyMs!.Value - 1;
var score = policy.LatencyWeight * speedupRatio - policy.PriceWeight * pricePremiumRatio;
```

When the minimum multiplier is zero or no positive finite latency exists, recommend the cheapest deterministic baseline. Sort score ties by multiplier, latency, success rate descending, then group ID. Expose an immutable group-ID-to-score dictionary for WinForms.

- [ ] **Step 4: Verify GREEN and regression compatibility**

Run the Core test command. Expected: every original `SelectCheapest` test and every weighted test passes.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/RoutingEngine.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: port weighted route evaluation from OnRightPath"
```

### Task 3: Explainable Decisions And Atomic Route State

**Files:**
- Create: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Create: `src/AIHubRouter.Core/RouteStateStore.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing decision and state tests**

Add cases for no candidates, first route, invalid current route, already optimal, cheaper winner, faster weighted winner, observed group overriding stale state, corrupt/missing JSON returning empty state, and an atomic save/load roundtrip.

```csharp
var result = RouteDecisionEngine.Decide(evaluation, new RouteState { CurrentGroupId = 1 }, now, 1);
Assert(result.Decision.Reason == RouteDecisionReason.FasterForWeightedTradeoff, "Wrong explanation.");
Assert(result.Decision.ShouldSwitch, "Weighted winner was not selected immediately.");
Assert(result.NextState.CurrentGroupId == result.Decision.Target?.Group.Id, "Next state diverged.");
```

- [ ] **Step 2: Verify RED**

Run the Core test command. Expected: compile failure for missing engine/store types.

- [ ] **Step 3: Implement decisions and Windows state storage**

Create:

```csharp
public sealed record RouteDecisionResult(RouteDecision Decision, RouteState NextState);

public static class RouteDecisionEngine
{
    public static RouteDecisionResult Decide(
        RouteEvaluation evaluation,
        RouteState state,
        DateTimeOffset now,
        long? observedCurrentGroupId = null);
}

public interface IRouteStateStore
{
    RouteState Load();
    void Save(RouteState state);
}
```

`JsonRouteStateStore.Load` catches JSON, I/O, and authorization failures and returns `new RouteState()`. `Save` writes `route-state.json.tmp`, then uses `File.Move(..., overwrite: true)` inside the supplied storage directory. Do not add Unix permissions or lock files.

- [ ] **Step 4: Verify GREEN**

Run the Core test command. Expected: all tests pass and the temporary test directory contains no lingering `.tmp` file.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/RouteDecisionEngine.cs src/AIHubRouter.Core/RouteStateStore.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: add explainable Win32 route decisions"
```

### Task 4: Authenticated Routing Service And Cache

**Files:**
- Modify: `src/AIHubRouter.Core/AIHubClient.cs`
- Create: `src/AIHubRouter.Core/RoutingService.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Add service fakes and failing behavioral tests**

Define `StubAIHubApiClient`, `StubAIHubClientFactory`, and `MemoryRouteStateStore` in the test program. Register tests proving:

```csharp
("Dry run never updates a Key", TestDryRunNeverUpdatesKey),
("Account data is cached but monitor data is fresh", TestAccountDataCache),
("Forced refresh bypasses account cache", TestForcedAccountRefresh),
("Business authentication failure retries once", TestBusinessAuthenticationRetry),
("Network failure never triggers login", TestRoutingNetworkFailureDoesNotLogin),
("Explicit empty Key selection is rejected", TestRoutingRejectsEmptySelection),
("Successful updates persist target state", TestSuccessfulRoutePersistsState),
("Partial update failure clears route certainty", TestPartialFailureClearsState),
```

Exercise this public contract:

```csharp
using var service = new RoutingService(
    settings,
    syntheticCredentials,
    stateStore,
    factory,
    persistCredentials: (_, _) => Task.CompletedTask,
    utcNow: () => now);
var result = service.RunOnceAsync(dryRun: true).GetAwaiter().GetResult();
Assert(client.UpdateCalls == 0, "Dry run performed a PUT.");
```

- [ ] **Step 2: Verify RED**

Run the Core test command. Expected: compile failures for `IAIHubApiClient`, factory, service, and cycle result.

- [ ] **Step 3: Add the client abstraction**

Make `AIHubClient` implement:

```csharp
public interface IAIHubApiClient : IDisposable
{
    Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default);
    Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default);
    Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default);
    Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default);
}
```

Add `IAIHubClientFactory.Create(baseUrl, bearerToken, cookie, userAgent)` and its production implementation.

- [ ] **Step 4: Implement one routing cycle**

Add `KeyRouteResult` and `RoutingCycleResult`, then implement `RoutingService.RunOnceAsync(bool dryRun, bool forceAccountRefresh, CancellationToken)`. Reuse `SessionCoordinator`; fetch public monitor data every cycle; cache groups/rates/Keys for `Math.Clamp(AccountCacheSeconds, 30, 3600)`; use `KeySelectionPolicy`; skip Keys already on target; never write in dry-run; retry one business authentication failure only when refresh/login is available; never retry network errors; save target state only when every intended update succeeds; save `CurrentGroupId = null` after partial failure.

- [ ] **Step 5: Verify GREEN**

Run the Core test command. Expected: service tests and all prior tests pass with no network requests.

- [ ] **Step 6: Commit**

```powershell
git add src/AIHubRouter.Core/AIHubClient.cs src/AIHubRouter.Core/RoutingService.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: add shared authenticated routing service"
```

### Task 5: Credential-Free Rotating Audit Log

**Files:**
- Create: `src/AIHubRouter.Core/AuditLogWriter.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing JSON, rotation, and exclusion tests**

Construct an explicit `RouteAuditEntry` with synthetic non-secret route data, write enough records to exceed a small byte threshold, parse each line with `JsonDocument`, assert `.1` rotation exists, and scan all output for `password`, `refresh`, `token`, `cookie`, `userAgent`, and synthetic secret values.

- [ ] **Step 2: Verify RED**

Run the Core test command. Expected: compile failure because the audit types do not exist.

- [ ] **Step 3: Implement explicit audit DTOs and rotation**

Expose only:

```csharp
public sealed record RouteAuditCandidate(long GroupId, double Multiplier, double? LatencyMs, double Score, bool Recommended);
public sealed record RouteAuditKey(long KeyId, bool Changed, bool Success, string? ErrorCode);
public sealed record RouteAuditEntry(DateTimeOffset Timestamp, RoutingMode Mode, RouteDecisionReason Reason,
    long? CurrentGroupId, long? TargetGroupId, bool DryRun,
    IReadOnlyList<RouteAuditCandidate> Candidates, IReadOnlyList<RouteAuditKey> Keys);
```

`AuditLogWriter` serializes only `RouteAuditEntry`, rotates before the incoming line exceeds `maximumBytes`, retains a clamped number of files, and never accepts an arbitrary object or credential-bearing model.

- [ ] **Step 4: Verify GREEN**

Run the Core test command. Expected: valid JSONL, bounded rotation, no sensitive property/value matches, all tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/AuditLogWriter.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: add credential-free route audit log"
```

### Task 6: Native WinForms Theme And Controls

**Files:**
- Create: `src/AIHubRouter.WinForms/NativeThemeManager.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`
- Modify: `src/AIHubRouter.WinForms/GridRows.cs`

- [ ] **Step 1: Add build-time contracts before implementation**

Add fields referencing the wished-for types so the WinForms build is RED:

```csharp
private readonly ComboBox _routingModeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
private readonly Button _simulateButton = new() { Text = "模拟", AutoSize = true };
private readonly ComboBox _themeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
private NativeThemePalette _activePalette = NativeThemeManager.LightPalette;
```

Extend `ProviderGridRow` with `double? WeightedScore` and `string DecisionState`.

- [ ] **Step 2: Verify RED**

Run: `dotnet build AIHubRouter.sln --no-restore -c Release`

Expected: compile failure for `NativeThemePalette` and `NativeThemeManager`.

- [ ] **Step 3: Implement native theme behavior**

`NativeThemeManager.Resolve(WinFormsTheme)` reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` only for `System`, defaults to light on any failure, and returns explicit light/dark palettes. `Apply(Control root, NativeThemePalette palette)` recursively colors forms, panels, labels, text boxes, combo boxes, numeric inputs, buttons, tabs, grids, tool/status strips, and dialogs using WinForms colors. It does not add Avalonia, Skia, WebView, SVG, or any package reference.

- [ ] **Step 4: Add compact native controls and stable columns**

Populate mode labels `经济 / 均衡 / 速度`, theme labels `跟随系统 / 浅色 / 深色`, wire the simulation button, and add provider columns for effective multiplier, first-token latency, weighted score, and recommendation. Keep fixed minimum widths so text does not overlap and preserve double-buffer controls.

- [ ] **Step 5: Verify GREEN**

Run the Release build. Expected: zero errors and zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/AIHubRouter.WinForms/NativeThemeManager.cs src/AIHubRouter.WinForms/MainForm.Layout.cs src/AIHubRouter.WinForms/GridRows.cs
git commit -m "feat: add native WinForms routing controls and themes"
```

### Task 7: WinForms Routing-Service Integration

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`

- [ ] **Step 1: Replace duplicated cycle ownership with `RoutingService`**

Create/dispose a service whenever auth or routing settings change. Use `%LocalAppData%\AIHubRouter\route-state.json` through `JsonRouteStateStore`, and `%LocalAppData%\AIHubRouter\logs\routing.jsonl` through `AuditLogWriter`. `刷新数据` calls a dry-run with forced account refresh, `模拟` calls a dry-run with normal cache behavior, and `立即路由` plus the timer call a real cycle.

- [ ] **Step 2: Preserve credential and Key persistence**

Map the current UI values to a new `PersistentAppSettings` while retaining `PersistCredentials`, `KeySelectionInitialized`, selected IDs including an empty array, vertical sync, polling interval, platform, and success threshold. Persist email/password/refresh/access token/Cookie/UA only through the existing DPAPI-backed `AppSettingsStore` callback.

- [ ] **Step 3: Render explainable results**

Bind provider score from `RoutingCycleResult.Evaluation.CandidateScores`, mark baseline/recommended/current states, refresh cached Key group values after real updates, and map decision reasons to concise Chinese status text. Dry-run status must explicitly say whether selected Keys would change; partial failures list Key names but never raw server messages.

- [ ] **Step 4: Wire and persist theme/mode events**

Initialize selectors from saved enum values, apply theme after `InitializeUi` and on selector changes, save settings on explicit save, and recreate the service after relevant setting changes. Keep `_verticalSyncCheck` behavior and existing buffer toggling unchanged.

- [ ] **Step 5: Build and manually smoke the native application**

Run:

```powershell
dotnet build AIHubRouter.sln --no-restore -c Release
dotnet run --project src/AIHubRouter.WinForms/AIHubRouter.WinForms.csproj -c Release
```

Expected: the native window opens; selectors and buttons remain stable at 100%, 125%, and 150% Windows scaling; follow-system/light/dark recolor all visible surfaces; simulation does not issue a PUT; vertical sync still toggles both grids.

- [ ] **Step 6: Commit**

```powershell
git add src/AIHubRouter.WinForms/MainForm.cs src/AIHubRouter.WinForms/MainForm.Layout.cs
git commit -m "feat: integrate weighted routing into native WinForms"
```

### Task 8: Attribution And Windows-Only Release Verification

**Files:**
- Modify: `README.md`
- Verify: `AIHubRouter.sln`
- Verify: `scripts/publish.ps1`
- Verify: `scripts/scan-release.ps1`

- [ ] **Step 1: Add visible downstream attribution**

Add a README section naming the weighted routing, decision explanation, audit, cache, and adaptive-theme concepts adapted from [`OnRightPath/AIHubRouter`](https://github.com/OnRightPath/AIHubRouter). State that this repository supports Windows WinForms only.

- [ ] **Step 2: Run the complete Core regression suite**

Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release`

Expected: all tests print `PASS`; exit code 0.

- [ ] **Step 3: Build cleanly**

Run: `dotnet build AIHubRouter.sln --no-restore -c Release`

Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Confirm the repository remains Windows-only**

Run:

```powershell
$forbidden = rg --files | Select-String 'AIHubRouter\.(Cli|Desktop)|Avalonia|Skia|systemd|\.service$|linux|osx|arm64'
if ($forbidden) { $forbidden; exit 1 }
```

Expected: no output, exit code 0. Confirm the solution lists only Core, WinForms, and Core.Tests.

- [ ] **Step 5: Publish both official Windows packages**

Run: `powershell -ExecutionPolicy Bypass -File scripts/publish.ps1`

Expected: the self-contained and lite staging/publish flows complete; each official artifact directory contains only `AIHubRouter.exe`.

- [ ] **Step 6: Re-run source and binary security gates**

Run the repository's scanner against source, staged DLLs, and EXEs exactly as invoked by `publish.ps1`. Expected: every scan reports clean; no email, password, token, Cookie, UA, refresh token, local username/path, or test secret enters an artifact.

- [ ] **Step 7: Review diff and commit documentation**

```powershell
git diff --check
git status --short
git add README.md
git commit -m "docs: credit OnRightPath Win32 feature ports"
```

- [ ] **Step 8: Request review and finish the branch**

Use `superpowers:requesting-code-review`, correct every confirmed issue with a new failing test first, then use `superpowers:verification-before-completion` and `superpowers:finishing-a-development-branch`. Push `codex/merge-win32-downstream` only after the final verification output is fresh.

## Self-Review Record

- Spec coverage: every included routing, caching, persistence, dry-run, audit, theme, attribution, security, and Windows release requirement maps to Tasks 1-8; every excluded platform/package is checked in Task 8.
- Completeness scan: every code-changing step contains the concrete contract, algorithm, or mapping it needs; no deferred implementation markers remain.
- Type consistency: `RoutingMode`, `WinFormsTheme`, `BalancedRoutingPolicy`, `RouteEvaluation.CandidateScores`, `RouteDecisionResult`, `IAIHubApiClient`, `RoutingCycleResult`, and audit DTO names are defined before their consumers.
- Security boundary: tests use temporary directories and synthetic credentials; the plan never reads `%LocalAppData%\AIHubRouter` credential files.
