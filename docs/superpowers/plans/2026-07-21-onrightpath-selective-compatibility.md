# OnRightPath v1.0.3 Selective Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adapt the provider-warning and route-stability behavior from `OnRightPath/AIHubRouter` v1.0.2/v1.0.3 into the native WinForms application without changing its local availability threshold, routing weights, default mode, or Windows-only release surface.

**Architecture:** Core deserializes warning metadata and owns a policy-driven minimum score advantage for route changes. WinForms consumes only safe derived warning state and the new decision reason; it never reflects warning messages. Existing `RoutingService`, authentication, persistence, audit, and release boundaries remain unchanged.

**Tech Stack:** C# 14, .NET 10, Windows Forms, `System.Text.Json`, executable Core test harness, PowerShell release gates.

---

## File Map

- Modify `src/AIHubRouter.Core/Models.cs`: warning DTO, `HasWarnings`, stability policy property, and decision reason.
- Modify `src/AIHubRouter.Core/RouteDecisionEngine.cs`: keep a valid current route when the weighted score lead is too small.
- Modify `src/AIHubRouter.Core/RoutingService.cs`: pass the active policy into the decision engine.
- Modify `tests/AIHubRouter.Core.Tests/Program.cs`: JSON, eligibility, threshold, stability, and regression scenarios.
- Modify `src/AIHubRouter.WinForms/MainForm.cs`: safe warning state and stability-reason text.
- Modify `README.md`: record compatibility with downstream v1.0.2/v1.0.3 while retaining the Windows-only statement.

### Task 1: Provider Warning Contract And Eligibility

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Register and write failing warning tests**

Add these registrations:

```csharp
("Provider warnings deserialize", TestProviderWarningsDeserialize),
("Warning provider remains eligible", TestWarningProviderRemainsEligible),
("Latest unavailable state remains ineligible", TestLatestUnavailableStateRemainsIneligible),
```

Add the JSON contract test:

```csharp
static void TestProviderWarningsDeserialize()
{
    var provider = JsonSerializer.Deserialize<ProviderStatus>("""
        {
          "id":"provider-1",
          "warningReasons":[{"type":"latency_spike","message":"synthetic warning","count":3}]
        }
        """)!;
    Assert(provider.HasWarnings, "Warning metadata was not recognized.");
    Assert(provider.WarningReasons.Single().Type == "latency_spike", "Warning type was not mapped.");
    Assert(provider.WarningReasons.Single().Count == 3, "Warning count was not mapped.");
}
```

Use synthetic providers to prove an enabled, available, fresh warning provider above the configured 6h threshold remains eligible, while `Available = false` remains ineligible even with a success rate of 1.0.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
```

Expected: compilation fails because `WarningReasons`, `ProviderWarningReason`, and `HasWarnings` do not exist.

- [ ] **Step 3: Add the warning model without changing filters**

Add to `ProviderStatus`:

```csharp
[JsonPropertyName("warningReasons")]
public List<ProviderWarningReason> WarningReasons { get; init; } = [];

public bool HasWarnings => WarningReasons.Count > 0;
```

Add:

```csharp
public sealed class ProviderWarningReason
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int? Count { get; init; }
}
```

Do not remove the existing `Enabled`, `Available`, freshness, authorization, finite-price, or `MinimumSuccessRate6h` filters in either `SelectCheapest` or `Evaluate`.

- [ ] **Step 4: Verify GREEN and the local threshold regression**

Run the Core tests. Expected: warning tests pass and the existing `Availability threshold` test still passes.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/Models.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: accept OnRightPath provider warnings"
```

### Task 2: Score-Advantage Stability Policy

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Modify: `src/AIHubRouter.Core/RoutingService.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing policy and decision tests**

Register:

```csharp
("Selective policy preserves local routing weights", TestSelectivePolicyPreservesLocalWeights),
("Close faster score keeps current group", TestCloseFasterScoreKeepsCurrentGroup),
("Close cheaper score keeps current group", TestCloseCheaperScoreKeepsCurrentGroup),
("Meaningful score advantage still switches", TestMeaningfulScoreAdvantageStillSwitches),
("Undefined score does not block a switch", TestUndefinedScoreDoesNotBlockSwitch),
```

The policy regression asserts:

```csharp
Assert(Policy(RoutingMode.Economy).PriceWeight == 0.95, "Economy weight changed.");
Assert(Policy(RoutingMode.Balanced).PriceWeight == 0.80, "Balanced weight changed.");
Assert(Policy(RoutingMode.Speed).PriceWeight == 0.35, "Speed weight changed.");
Assert(new PersistentAppSettings().RoutingMode == RoutingMode.Economy, "Default mode changed.");
Assert(Policy(RoutingMode.Balanced).MinimumScoreAdvantageToSwitch == 0.05,
    "Stability threshold changed.");
```

For close faster routes use equal `0.02` multipliers and `1000` versus `980` ms. For close cheaper routes use the current route at `0.0201` / `981` ms and the recommended route at `0.02` / `1000` ms. In both cases assert `ShouldSwitch == false`, `Target` is the current group, and the reason is `ScoreAdvantageTooSmall`.

For a meaningful advantage use equal `0.02` multipliers and `1000` versus `400` ms; assert the target switches immediately. For undefined scores use a zero-price recommendation and assert the stability threshold does not block the existing zero-price decision.

- [ ] **Step 2: Run tests and verify RED**

Run the Core test command. Expected: compilation fails for `MinimumScoreAdvantageToSwitch`, `MinimumScoreAdvantageOverride`, and `ScoreAdvantageTooSmall`, or the close-score behavior fails because it still switches.

- [ ] **Step 3: Add policy properties and validation**

Add to `BalancedRoutingPolicy` without changing `PriceWeight`:

```csharp
public const double DefaultMinimumScoreAdvantageToSwitch = 0.05;
public double? MinimumScoreAdvantageOverride { get; init; }
public double MinimumScoreAdvantageToSwitch =>
    MinimumScoreAdvantageOverride ?? DefaultMinimumScoreAdvantageToSwitch;
```

Extend `Validate()`:

```csharp
if (MinimumScoreAdvantageOverride is { } advantage &&
    (advantage < 0 || !double.IsFinite(advantage)))
{
    throw new ArgumentOutOfRangeException(nameof(MinimumScoreAdvantageOverride));
}
```

Add `ScoreAdvantageTooSmall` to `RouteDecisionReason`.

- [ ] **Step 4: Apply the threshold in the decision engine**

Change the public signature to:

```csharp
public static RouteDecisionResult Decide(
    RouteEvaluation evaluation,
    RouteState state,
    BalancedRoutingPolicy policy,
    DateTimeOffset now,
    long? observedCurrentGroupId = null)
```

Validate `policy`, then after the current/target validity and already-optimal branches, read scores from `evaluation.CandidateScores`. If both scores exist and:

```csharp
targetScore - currentScore <= policy.MinimumScoreAdvantageToSwitch
```

return a non-switch decision targeting `current`, reason `ScoreAdvantageTooSmall`, current route state, the current route premium, and zero latency improvement. Otherwise preserve the current `BetterPrice` / `FasterForWeightedTradeoff` behavior.

In `RoutingService`, create the policy once, pass it to both `RoutingEngine.Evaluate` and `RouteDecisionEngine.Decide`:

```csharp
var policy = _settings.CreatePolicy();
var evaluation = RoutingEngine.Evaluate(summary.Apis, _cachedGroups, _cachedRates, policy, now);
var decisionResult = RouteDecisionEngine.Decide(evaluation, _stateStore.Load(), policy, now, observedGroupId);
```

Update every test call to pass the same policy used to create its evaluation.

- [ ] **Step 5: Verify GREEN and service regressions**

Run the Core tests. Expected: all close-score, meaningful-score, initial route, invalid route, zero price, unknown latency, dry-run, partial failure, and mixed-Key tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/AIHubRouter.Core/Models.cs src/AIHubRouter.Core/RouteDecisionEngine.cs src/AIHubRouter.Core/RoutingService.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: stabilize compatible route decisions"
```

### Task 3: Native WinForms Status Integration

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Add a safe warning-state helper contract**

Extract the final provider-state decoration into a Core helper so it can be tested without launching WinForms:

```csharp
public static string DecorateRoutableState(string state, ProviderStatus provider) =>
    state == "可路由" && provider.HasWarnings ? "可路由（警告）" : state;
```

Place this focused helper in `ProviderStatusPresentation.cs` under Core. Register a test that passes a warning whose message contains `synthetic-sensitive-warning` and asserts the returned state is exactly `可路由（警告）` and does not contain the message.

- [ ] **Step 2: Verify RED**

Run the Core test command. Expected: compilation fails because `ProviderStatusPresentation` does not exist.

- [ ] **Step 3: Implement and consume safe state decoration**

Create `src/AIHubRouter.Core/ProviderStatusPresentation.cs` with the helper above. In both WinForms provider-row construction paths, apply it only after the existing eligibility state has been calculated:

```csharp
State = ProviderStatusPresentation.DecorateRoutableState(state, provider)
```

Add to `DecisionReasonText`:

```csharp
RouteDecisionReason.ScoreAdvantageTooSmall => "优势较小，保持当前路由",
```

Do not read `ProviderWarningReason.Message` in WinForms or audit code.

- [ ] **Step 4: Verify tests and Release build**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
dotnet build AIHubRouter.sln --no-restore -c Release
```

Expected: all tests pass; build reports 0 warnings and 0 errors.

- [ ] **Step 5: Commit**

```powershell
git add src/AIHubRouter.Core/ProviderStatusPresentation.cs src/AIHubRouter.WinForms/MainForm.cs tests/AIHubRouter.Core.Tests/Program.cs
git commit -m "feat: show compatible provider warning state"
```

### Task 4: Attribution, Review, And Windows Release Gates

**Files:**
- Modify: `README.md`
- Verify: `scripts/publish.ps1`
- Verify: `AIHubRouter.sln`

- [ ] **Step 1: Update compatibility attribution**

Extend the existing downstream contribution paragraph to state that provider-warning and route-stability behavior is selectively compatible with downstream v1.0.2/v1.0.3, while local weights, the 6h threshold, and Windows-only support remain authoritative.

- [ ] **Step 2: Run complete verification**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
dotnet build AIHubRouter.sln --no-restore -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

Expected: all Core tests pass, the build has 0 warnings/errors, and both Windows artifacts pass source/DLL/EXE scans.

- [ ] **Step 3: Confirm excluded platforms remain absent**

Run:

```powershell
$forbidden = rg --files | Select-String 'AIHubRouter\.(Cli|Desktop)|Avalonia|Skia|systemd|\.service$|linux|osx|arm64'
if ($forbidden) { $forbidden; exit 1 }
```

Expected: no output and exit code 0.

- [ ] **Step 4: Request independent review**

Use `superpowers:requesting-code-review` against base `bcf9270` and the compatibility branch head. Fix every confirmed Critical or Important issue with a failing test first.

- [ ] **Step 5: Commit documentation and push a PR**

```powershell
git add README.md
git commit -m "docs: record OnRightPath v1.0.3 compatibility"
git diff --check
git status --short
git push -u origin codex/sync-onrightpath-v1.0.3
gh pr create --base master --head codex/sync-onrightpath-v1.0.3
```

## Self-Review Record

- Spec coverage: warning JSON, latest state, retained local threshold/weights/default, stability threshold, safe WinForms state, attribution, exclusions, and release verification all map to Tasks 1-4.
- Completeness: every implementation step defines exact types, signatures, branch conditions, commands, and expected results.
- Type consistency: `ProviderWarningReason`, `HasWarnings`, `MinimumScoreAdvantageToSwitch`, `ScoreAdvantageTooSmall`, the policy-aware `Decide` signature, and `ProviderStatusPresentation` are defined before use.
- Security: no UI or audit path consumes downstream warning messages; tests use synthetic warning content only.
