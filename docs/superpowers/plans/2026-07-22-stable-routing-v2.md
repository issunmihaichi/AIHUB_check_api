# Stable Routing V2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add forced-recovery classification, policy-switch hysteresis, conservative performance confidence, and auditable decisions to the Win32 routing engine.

**Architecture:** Keep forced recovery in `RouteDecisionEngine` before all policy guards. Persist the small hysteresis state in `RouteState`; derive confidence-aware provider values in the existing rolling window; expose all resulting state through `RouteDecision` and JSONL audit entries.

**Tech Stack:** .NET 8, C# records, existing console core-test runner, WinForms.

---

### Task 1: Persist and apply policy-switch hysteresis

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Test: `tests/AIHubRouter.Core.Tests/CoreTestCases.AdaptiveSwitching.cs`
- Test: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing tests for dwell, completed evaluation count, candidate stability, and forced recovery bypass.**
- [ ] **Step 2: Run the focused core test runner and confirm the tests fail because the state and reasons do not exist.**
- [ ] **Step 3: Add persisted hysteresis fields and enforce them only for eligible-to-eligible policy switches.**
- [ ] **Step 4: Re-run the focused core test runner and confirm the tests pass.**

### Task 2: Add confidence-aware provider performance

**Files:**
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `src/AIHubRouter.Core/ProviderMetricsRollingWindow.cs`
- Modify: `src/AIHubRouter.Core/BalancedDeadlineEngine.cs`
- Modify: `src/AIHubRouter.Core/RouteDecisionEngine.cs`
- Test: `tests/AIHubRouter.Core.Tests/CoreTestCases.ProviderMetrics.cs`
- Test: `tests/AIHubRouter.Core.Tests/CoreTestCases.AdaptiveSwitching.cs`
- Test: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing tests for P90 TTFT, P25 output speed, sample counts, and low-sample speed rejection.**
- [ ] **Step 2: Run the focused tests and confirm the tests fail with missing performance metadata.**
- [ ] **Step 3: Aggregate the metrics per platform/group and use conservative values for deadline and speed checks.**
- [ ] **Step 4: Re-run the focused tests and confirm the tests pass.**

### Task 3: Preserve user mode and enrich audit output

**Files:**
- Modify: `src/AIHubRouter.Core/AdaptiveSwitchDecisionEngine.cs`
- Modify: `src/AIHubRouter.Core/AuditLogWriter.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Routing.cs`
- Test: `tests/AIHubRouter.Core.Tests/CoreTestCases.AdaptiveSwitching.cs`
- Test: `tests/AIHubRouter.Core.Tests/CoreTestCases.Routing.cs`
- Test: `tests/AIHubRouter.Core.Tests/TestCatalog.cs`

- [ ] **Step 1: Write failing tests proving frequent calls retain Economy and proving audit JSON includes the decision class and confidence fields.**
- [ ] **Step 2: Run the focused tests and confirm the tests fail for the old interval override and missing fields.**
- [ ] **Step 3: Restrict interval adaptation to Balanced/Speed and write the enriched decision data to the existing JSONL log.**
- [ ] **Step 4: Re-run focused tests and confirm the tests pass.**

### Task 4: Verify release behavior

**Files:**
- Modify: `docs/ALGORITHM_MODES.md`
- Test: `tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj`

- [ ] **Step 1: Document forced recovery, hysteresis, confidence, and data limits without claiming unsupported per-model metrics.**
- [ ] **Step 2: Run `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release`.**
- [ ] **Step 3: Run `dotnet build AIHubRouter.sln -c Release` and `git diff --check`.**
