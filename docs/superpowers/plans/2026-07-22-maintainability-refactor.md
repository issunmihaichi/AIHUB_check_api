# Maintainability Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the monolithic WinForms orchestrator and custom test entry point into focused source files without changing observable application behavior.

**Architecture:** Keep the WinForms form as one partial type so existing private control state and event registration are preserved. Convert the test project from top-level local functions to an internal test catalog plus a partial test-case type, allowing domain files to share one deterministic test runner and fixture set.

**Tech Stack:** C# 14, .NET 10, Windows Forms, existing zero-dependency console test executable.

---

### Task 1: Establish A Test Catalog

**Files:**
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs:1-142`
- Create: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs:1-142`

- [ ] **Step 1: Add the failing duplicate-name characterization test to the existing test list.**

  Add `("Test catalog rejects duplicate names", TestCatalogRejectsDuplicateNames)` after the two credential-parser tests and add this local function before `TestBearerNormalization`:

  ```csharp
  static void TestCatalogRejectsDuplicateNames()
  {
      try
      {
          TestCatalog.Create(
              new TestCase("duplicate", static () => { }),
              new TestCase("duplicate", static () => { }));
          throw new InvalidOperationException("Duplicate test names were accepted.");
      }
      catch (InvalidOperationException exception)
      {
          Assert(exception.Message == "Duplicate test name: duplicate.",
              "Duplicate test catalog error was not deterministic.");
      }
  }
  ```

- [ ] **Step 2: Verify the test fails for the intended reason.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: compilation fails because `TestCatalog` and `TestCase` do not exist yet.

- [ ] **Step 3: Add the catalog implementation.**

  Create `TestCatalog.cs`:

  ```csharp
  namespace AIHubRouter.Core.Tests;

  internal readonly record struct TestCase(string Name, Action Body);

  internal static class TestCatalog
  {
      public static IReadOnlyList<TestCase> Create(params TestCase[] tests)
      {
          var names = new HashSet<string>(StringComparer.Ordinal);
          foreach (var test in tests)
          {
              if (!names.Add(test.Name))
              {
                  throw new InvalidOperationException($"Duplicate test name: {test.Name}.");
              }
          }

          return Array.AsReadOnly(tests);
      }
  }
  ```

  Add `using AIHubRouter.Core.Tests;` to `Program.cs` after its using directives so the new test type is visible. Do not add a namespace declaration to a file that contains top-level statements.

- [ ] **Step 4: Verify the new test and existing suite pass.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: every existing `PASS` line remains and `PASS Test catalog rejects duplicate names` appears exactly once.

- [ ] **Step 5: Commit the catalog seam.**

  ```powershell
  git add tests/AIHubRouter.Core.Tests/Program.cs tests/AIHubRouter.Core.Tests/TestCatalog.cs
  git commit -m "refactor: add deterministic core test catalog"
  ```

### Task 2: Split The Core Test Runner And Fixtures

**Files:**
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs:1-2444`
- Modify: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.cs`
- Create: `tests/AIHubRouter.Core.Tests/TestFixtures.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Convert the top-level runner to a small program.**

  Leave only `using AIHubRouter.Core;`, `using System.Net;`, `using System.Text;`, and `using System.Text.Json;` in the new files that need them. Replace the top-level `tests` tuple declaration and execution loop with:

  ```csharp
  var failures = 0;
  foreach (var test in TestCatalog.All)
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
          TestFixtures.Assert(summary.Apis.Count > 0, "Public provider endpoint returned no entries.");
          Console.WriteLine($"PASS Public API smoke test ({summary.Apis.Count} entries)");
      }
      catch (Exception exception)
      {
          failures++;
          Console.Error.WriteLine($"FAIL Public API smoke test: {exception.Message}");
      }
  }

  return failures == 0 ? 0 : 1;
  ```

- [ ] **Step 2: Move shared helpers and stubs into `TestFixtures.cs`.**

  Create `internal static class TestFixtures` containing `JsonResponse`, `Provider`, `Group`, `Criteria`, `Policy`, and `Assert` from current `Program.cs:2216-2295`. Move `StubHttpMessageHandler`, `MemoryRouteStateStore`, `StubRoutingClientFactory`, and `StubRoutingClient` from current `Program.cs:2297-2444` unchanged into the same namespace. Update references in the test case files to either use `using static AIHubRouter.Core.Tests.TestFixtures;` or `TestFixtures.` explicitly.

- [ ] **Step 3: Move every current `Test*` body into `internal static partial class CoreTestCases`.**

  Create `CoreTestCases.cs` with exactly:

  ```csharp
  namespace AIHubRouter.Core.Tests;

  internal static partial class CoreTestCases
  {
  }
  ```

  Move every current `Test*` body, including `TestCatalogRejectsDuplicateNames`, into this type before splitting files in Task 3. Preserve method names, static signatures, and assertion text exactly. Change the catalog entries to method groups such as `new TestCase("Bearer token normalization", CoreTestCases.TestBearerNormalization)`.

- [ ] **Step 4: Publish the full deterministic catalog.**

  Add this exact catalog to `TestCatalog.cs`, retaining every current display name and order from `Program.cs:6-111`:

  ```csharp
  public static IReadOnlyList<TestCase> All { get; } = Create(
      new("Bearer token normalization", CoreTestCases.TestBearerNormalization),
      new("Token extraction from cookie", CoreTestCases.TestCookieTokenExtraction),
      new("Test catalog rejects duplicate names", CoreTestCases.TestCatalogRejectsDuplicateNames),
      new("Lowest available authorized group", CoreTestCases.TestLowestAvailableGroup),
      new("User rate override", CoreTestCases.TestUserRateOverride),
      new("Availability threshold", CoreTestCases.TestAvailabilityThreshold),
      new("Provider warnings deserialize", CoreTestCases.TestProviderWarningsDeserialize),
      new("Null provider warnings are tolerated", CoreTestCases.TestNullProviderWarningsAreTolerated),
      new("Provider last-call aliases deserialize", CoreTestCases.TestProviderLastCallAliasesDeserialize),
      new("Twenty-minute provider metrics use medians", CoreTestCases.TestRollingProviderMetricsUseMedians),
      new("Rolling provider metrics discard samples outside the window", CoreTestCases.TestRollingProviderMetricsDiscardExpiredSamples),
      new("Rolling provider metrics clear account history", CoreTestCases.TestRollingProviderMetricsClearHistory),
      new("Rolling provider metrics are stable for duplicate groups", CoreTestCases.TestRollingProviderMetricsAreStableForDuplicateGroups),
      new("Adaptive constants match supplied economics", CoreTestCases.TestAdaptiveConstants),
      new("Adaptive preference follows interval boundaries", CoreTestCases.TestAdaptivePreferenceBoundaries),
      new("Current-group interval uses latest provider call", CoreTestCases.TestCurrentGroupIntervalResolution),
      new("Missing call time retains base preference", CoreTestCases.TestMissingCallTimeRetainsBasePreference),
      new("Adaptive penalty uses new multiplier", CoreTestCases.TestAdaptivePenalty),
      new("Adaptive completion time includes TTFT", CoreTestCases.TestAdaptiveCompletionTime),
      new("Adaptive net saving subtracts context penalty", CoreTestCases.TestAdaptiveNetSaving),
      new("Adaptive cost accepts positive saving", CoreTestCases.TestAdaptiveCostAcceptsPositiveSaving),
      new("Adaptive cost rejects slow candidate", CoreTestCases.TestAdaptiveCostRejectsSlowCandidate),
      new("Adaptive balanced requires all safeguards", CoreTestCases.TestAdaptiveBalancedSafeguards),
      new("Adaptive speed accepts generation boost", CoreTestCases.TestAdaptiveSpeedAcceptsGenerationBoost),
      new("Adaptive speed accepts end-to-end gain", CoreTestCases.TestAdaptiveSpeedAcceptsEndToEndGain),
      new("Balanced deadline uses explicit output budget", CoreTestCases.TestBalancedDeadlineUsesExplicitOutputBudget),
      new("Balanced deadline keeps current feasible node", CoreTestCases.TestBalancedDeadlineKeepsCurrentNode),
      new("Balanced deadline switches to cheapest feasible node", CoreTestCases.TestBalancedDeadlineChoosesCheapestFeasibleNode),
      new("Balanced deadline cold start chooses cheapest feasible node", CoreTestCases.TestBalancedDeadlineColdStart),
      new("Balanced deadline honors user soft tolerance", CoreTestCases.TestBalancedDeadlineHonorsSoftTolerance),
      new("Balanced deadline zero falls back to economy", CoreTestCases.TestBalancedDeadlineZeroFallsBackToEconomy),
      new("Adaptive short task is protected outside cost", CoreTestCases.TestAdaptiveShortTaskProtection),
      new("Adaptive invalid performance cannot switch", CoreTestCases.TestAdaptiveInvalidPerformance),
      new("Adaptive invalid old performance cannot satisfy relative time", CoreTestCases.TestAdaptiveInvalidOldPerformance),
      new("Warning provider remains eligible", CoreTestCases.TestWarningProviderRemainsEligible),
      new("Latest unavailable state remains ineligible", CoreTestCases.TestLatestUnavailableStateRemainsIneligible),
      new("Warning presentation excludes server message", CoreTestCases.TestWarningPresentationExcludesServerMessage),
      new("Warning decoration requires routable latest state", CoreTestCases.TestWarningDecorationRequiresRoutableLatestState),
      new("Routing presentation preserves availability threshold", CoreTestCases.TestRoutingPresentationPreservesAvailabilityThreshold),
      new("Routing presentation rejects invalid effective rate", CoreTestCases.TestRoutingPresentationRejectsInvalidEffectiveRate),
      new("Stale status rejection", CoreTestCases.TestStaleStatusRejection),
      new("Routing preferences default to Win32-compatible values", CoreTestCases.TestRoutingPreferenceDefaults),
      new("Routing preferences roundtrip", CoreTestCases.TestRoutingPreferenceRoundtrip),
      new("Balanced mode buys meaningful latency", CoreTestCases.TestBalancedModeBuysLatency),
      new("Balanced mode keeps price for moderate speed gap", CoreTestCases.TestBalancedModeKeepsPriceForModerateSpeedGap),
      new("Economy mode protects price", CoreTestCases.TestEconomyModeProtectsPrice),
      new("Speed mode accepts larger price premium", CoreTestCases.TestSpeedModeAcceptsLargerPremium),
      new("Selective policy preserves local routing weights", CoreTestCases.TestSelectivePolicyPreservesLocalWeights),
      new("Cost mode proposes strict cheapest candidate", CoreTestCases.TestCostModeProposesCheapest),
      new("Cost mode falls back when cheapest TTFT is unknown", CoreTestCases.TestCostModeFallsBackWhenCheapestTtftIsUnknown),
      new("Economy remains strict during frequent calls", CoreTestCases.TestFrequentCallsOverrideEconomy),
      new("Idle calls override speed with cost", CoreTestCases.TestIdleCallsOverrideSpeed),
      new("Adaptive rejection keeps current group", CoreTestCases.TestAdaptiveRejectionKeepsCurrentGroup),
      new("Adaptive acceptance updates selected Keys", CoreTestCases.TestAdaptiveAcceptanceUpdatesKeys),
      new("Adaptive traversal finds an accepted candidate beyond weighted winner", CoreTestCases.TestAdaptiveTraversalFindsAcceptedCandidateBeyondWeightedWinner),
      new("Adaptive rankings follow accepted algorithm order", CoreTestCases.TestAdaptiveRankingsFollowAcceptedAlgorithmOrder),
      new("Initial and invalid routes recover immediately", CoreTestCases.TestAdaptiveRecoveryBypassesGuard),
      new("Missing latency ranks last", CoreTestCases.TestMissingLatencyRanksLast),
      new("Invalid measurements are excluded", CoreTestCases.TestInvalidMeasurementsAreExcluded),
      new("Extreme latency scores stay finite", CoreTestCases.TestExtremeLatencyScoresStayFinite),
      new("Zero multiplier remains free", CoreTestCases.TestZeroMultiplierWindow),
      new("Initial route has an explainable reason", CoreTestCases.TestInitialRouteDecision),
      new("Preview and simulation share the initial route decision", CoreTestCases.TestPreviewAndSimulationShareInitialDecision),
      new("Adaptive speed winner switches after guard", CoreTestCases.TestWeightedSpeedWinnerSwitchesImmediately),
      new("Already optimal route does not switch", CoreTestCases.TestAlreadyOptimalRouteDecision),
      new("Invalid current route switches", CoreTestCases.TestInvalidCurrentRouteDecision),
      new("No candidate keeps route state", CoreTestCases.TestNoCandidateDecision),
      new("Route state persists atomically", CoreTestCases.TestRouteStateRoundtrip),
      new("Unreadable route state resets safely", CoreTestCases.TestUnreadableRouteStateResets),
      new("Dry run never updates a Key", CoreTestCases.TestDryRunNeverUpdatesKey),
      new("Account data is cached but monitor data is fresh", CoreTestCases.TestAccountDataCache),
      new("Routing result exposes cached user rates", CoreTestCases.TestRoutingResultExposesUserRates),
      new("Forced refresh bypasses account cache", CoreTestCases.TestForcedAccountRefresh),
      new("Business authentication failure retries once", CoreTestCases.TestBusinessAuthenticationRetry),
      new("Network failure never triggers login", CoreTestCases.TestRoutingNetworkFailureDoesNotLogin),
      new("Explicit empty Key selection is rejected", CoreTestCases.TestRoutingRejectsEmptySelection),
      new("Successful updates persist target state", CoreTestCases.TestSuccessfulRoutePersistsState),
      new("Partial update failure clears route certainty", CoreTestCases.TestPartialFailureClearsState),
      new("Already optimal cycle reports selected Keys", CoreTestCases.TestAlreadyOptimalReportsSelectedKeys),
      new("Mixed selected groups reconcile to target", CoreTestCases.TestMixedSelectedGroupsReconcile),
      new("Audit log writes valid JSON and rotates safely", CoreTestCases.TestAuditLogWritesValidJsonAndRotates),
      new("Publish script checks native exit codes", CoreTestCases.TestPublishScriptChecksNativeExitCodes),
      new("Encrypted settings roundtrip", CoreTestCases.TestEncryptedSettingsRoundtrip),
      new("Usable access token is reused", CoreTestCases.TestUsableAccessTokenIsReused),
      new("Expired access token refreshes first", CoreTestCases.TestExpiredAccessTokenRefreshesFirst),
      new("Rejected refresh falls back to login", CoreTestCases.TestRejectedRefreshFallsBackToLogin),
      new("Refresh API code falls back to login", CoreTestCases.TestRefreshApiCodeFallsBackToLogin),
      new("Refresh network failure does not log in", CoreTestCases.TestRefreshNetworkFailureDoesNotLogIn),
      new("Authentication API code is classified", CoreTestCases.TestAuthenticationApiCodeIsClassified),
      new("Login endpoint maps session", CoreTestCases.TestLoginEndpointMapsSession),
      new("Refresh endpoint maps rotated session", CoreTestCases.TestRefreshEndpointMapsRotatedSession),
      new("Refresh keeps token when server omits rotation", CoreTestCases.TestRefreshKeepsTokenWhenServerOmitsRotation),
      new("Authentication error hides server message", CoreTestCases.TestAuthenticationErrorHidesServerMessage),
      new("Business error hides server message", CoreTestCases.TestBusinessErrorHidesServerMessage),
      new("Authentication statuses have safe diagnostics", CoreTestCases.TestAuthenticationStatusesHaveSafeDiagnostics),
      new("Business authentication statuses keep business diagnostics", CoreTestCases.TestBusinessAuthenticationStatusesKeepBusinessDiagnostics),
      new("Malformed authentication responses retain endpoint context", CoreTestCases.TestMalformedAuthenticationResponsesRetainEndpointContext),
      new("Unknown errors do not expose credential text", CoreTestCases.TestUnknownErrorsDoNotExposeCredentialText),
      new("Interactive login requirement is rejected", CoreTestCases.TestInteractiveLoginRequirementIsRejected),
      new("Empty key selection roundtrips", CoreTestCases.TestEmptyKeySelectionRoundtrips),
      new("First key selection chooses first active key", CoreTestCases.TestFirstKeySelectionChoosesFirstActiveKey),
      new("Initialized empty key selection stays empty", CoreTestCases.TestInitializedEmptyKeySelectionStaysEmpty));
  ```

  Do not add, remove, rename, or reorder the existing test cases other than inserting the catalog characterization test from Task 1.

- [ ] **Step 5: Verify the test runner refactor.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: every previous test name passes in its original order, followed by no unhandled exception or duplicate catalog entry.

- [ ] **Step 6: Commit the runner and fixtures split.**

  ```powershell
  git add tests/AIHubRouter.Core.Tests
  git commit -m "refactor: separate core test runner and fixtures"
  ```

### Task 3: Divide Test Cases By Domain

**Files:**
- Modify: `tests/AIHubRouter.Core.Tests/CoreTestCases.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.ProviderMetrics.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.AdaptiveSwitching.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.BalancedDeadline.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.Routing.cs`
- Create: `tests/AIHubRouter.Core.Tests/CoreTestCases.PersistenceAndAuth.cs`
- Test: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Move provider metrics tests without changing methods.**

  Put `TestRollingProviderMetricsUseMedians`, `TestRollingProviderMetricsDiscardExpiredSamples`, `TestRollingProviderMetricsClearHistory`, and `TestRollingProviderMetricsAreStableForDuplicateGroups` into `CoreTestCases.ProviderMetrics.cs` inside `internal static partial class CoreTestCases`.

- [ ] **Step 2: Move adaptive and deadline tests without changing methods.**

  Put all `TestAdaptive*`, `TestCurrentGroupIntervalResolution`, `TestMissingCallTimeRetainsBasePreference`, and `TestBalancedDeadline*` methods into the corresponding adaptive-switching or balanced-deadline partial file. Keep `TestBalancedMode*`, `TestEconomyModeProtectsPrice`, and `TestSpeedModeAcceptsLargerPremium` with balanced-deadline tests because they characterize `RoutingEngine` policy weights.

- [ ] **Step 3: Move route decision and routing service tests without changing methods.**

  Put tests from `TestSelectivePolicyPreservesLocalWeights` through `TestMixedSelectedGroupsReconcile` in `CoreTestCases.Routing.cs`, preserving the current test bodies and catalog ordering.

- [ ] **Step 4: Move persistence, release-scan, session, and error-presentation tests without changing methods.**

  Put `TestAuditLogWritesValidJsonAndRotates`, `TestPublishScriptChecksNativeExitCodes`, `TestEncryptedSettingsRoundtrip`, all session refresh/login tests, all safe-error tests, and all key-selection tests in `CoreTestCases.PersistenceAndAuth.cs`.

- [ ] **Step 5: Verify the domain split.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: the catalog order and names match Task 2; all test cases pass.

- [ ] **Step 6: Commit the domain test files.**

  ```powershell
  git add tests/AIHubRouter.Core.Tests
  git commit -m "refactor: group core tests by domain"
  ```

### Task 4: Extract WinForms Authentication And Routing Partials

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs:163-1378`
- Create: `src/AIHubRouter.WinForms/MainForm.Authentication.cs`
- Create: `src/AIHubRouter.WinForms/MainForm.Routing.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Move authentication methods into `MainForm.Authentication.cs`.**

  Create the partial type in the existing `AIHubRouter.WinForms` namespace. Move these methods unchanged: `ValidateAuthenticationAsync`, `PersistRoutingCredentialsAsync`, `ResetAuthenticationAndRoutingService`, `InvalidateRoutingService`, `CreateManualClient`, `HasCredentials`, `HasAutomaticCredentials`, `CreateAuthenticatedClientAsync`, `PersistSessionAsync`, `RunAuthenticatedAsync`, `CanRenewAutomatically`, `InvalidateCurrentSession`, and `FindIdentity`.

- [ ] **Step 2: Move routing methods into `MainForm.Routing.cs`.**

  Move these methods unchanged: `RefreshDataAsync`, `RefreshDataCoreAsync`, `ExecuteRoutingCycleAsync`, `EnsureRoutingService`, `ApplyRoutingCycleResult`, `WriteAudit`, `ExecuteRoutingCoreAsync`, `RecalculateCandidate`, `ApplyProviders`, `ApplyKeys`, `CurrentKeyRows`, `HandleKeySelectionChanged`, `CaptureKeySelection`, and `ToggleAutoRoutingAsync`.

- [ ] **Step 3: Keep the form coordinator narrow.**

  Leave fields, constructor, and `WireEvents` in `MainForm.cs`. Keep the existing `FormClosing` lambda inside `WireEvents` unchanged, including timer shutdown, routing-service disposal, cancellation, and tooltip disposal. Retain every event subscription expression byte-for-byte in behavior, including timer and grid handlers.

- [ ] **Step 4: Verify the extraction.**

  Run: `dotnet build AIHubRouter.sln --no-restore -c Release`

  Expected: successful build with no warnings or errors.

- [ ] **Step 5: Run the behavioral suite.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: all tests pass; this confirms routing and authentication core behavior is unchanged.

- [ ] **Step 6: Commit the WinForms workflow split.**

  ```powershell
  git add src/AIHubRouter.WinForms/MainForm.cs src/AIHubRouter.WinForms/MainForm.Authentication.cs src/AIHubRouter.WinForms/MainForm.Routing.cs
  git commit -m "refactor: separate form authentication and routing workflows"
  ```

### Task 5: Extract WinForms Settings And Presentation Partials

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs:510-1378`
- Create: `src/AIHubRouter.WinForms/MainForm.Settings.cs`
- Create: `src/AIHubRouter.WinForms/MainForm.Presentation.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Move settings and countdown ownership into `MainForm.Settings.cs`.**

  Move these methods unchanged: `BuildPersistentSettings`, `CurrentRoutingMode`, `ModeDisplayName`, `CurrentDurationCategory`, `ApplySelectedTheme`, `UpdateTimerInterval`, `GetBalancedRemainingSeconds`, `RestartBalancedCountdown`, `UpdateBalancedCountdownDisplay`, `ApplySmoothRendering`, `LoadSavedSettings`, `SaveCurrentSettings`, and `HandlePersistenceChanged`.

- [ ] **Step 2: Move presentation helpers into `MainForm.Presentation.cs`.**

  Move these methods unchanged: `PreferenceDisplayName`, `ResolveAdaptiveRank`, `DecisionReasonText`, `ToggleCredentialVisibility`, `ShowAuthenticationGuide`, `OpenLoginPage`, `PasteCredential`, `SetBusy`, `SetStatus`, and `HandleError`.

- [ ] **Step 3: Verify control boundaries remain intact.**

  Run: `dotnet build AIHubRouter.sln --no-restore -c Release`

  Expected: successful build with no warnings or errors; compiler access to every control field proves the partial class boundaries preserve the existing form instance.

- [ ] **Step 4: Run the full core suite.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: all test cases pass.

- [ ] **Step 5: Commit the settings and presentation split.**

  ```powershell
  git add src/AIHubRouter.WinForms/MainForm.cs src/AIHubRouter.WinForms/MainForm.Settings.cs src/AIHubRouter.WinForms/MainForm.Presentation.cs
  git commit -m "refactor: separate form settings and presentation"
  ```

### Task 6: Final Verification And Review

**Files:**
- Verify: `AIHubRouter.sln`
- Verify: `tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj`

- [ ] **Step 1: Check whitespace and source diff.**

  Run: `git diff --check HEAD~5 HEAD`

  Expected: no output and exit code `0`.

- [ ] **Step 2: Run the full test executable.**

  Run: `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`

  Expected: every catalog entry prints `PASS`; process exit code is `0`.

- [ ] **Step 3: Build the Windows Release configuration.**

  Run: `dotnet build AIHubRouter.sln --no-restore -c Release`

  Expected: `0` warnings and `0` errors.

- [ ] **Step 4: Review behavioral boundaries.**

  Confirm the diff changes only source-file ownership, test organization, and the catalog duplicate guard. Confirm no values from `AdaptiveSwitchDecisionEngine`, `BalancedDeadlineEngine`, `ProviderMetricsRollingWindow`, `RoutingEngine`, `AIHubClient`, or the release scripts changed.

- [ ] **Step 5: Commit final formatting only if needed.**

  ```powershell
  git add src tests
  git commit -m "style: finalize maintainability refactor"
  ```
