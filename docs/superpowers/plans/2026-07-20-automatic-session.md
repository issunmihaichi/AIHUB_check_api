# AIHub Automatic Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add encrypted email/password authentication with refresh-first session renewal, persist selected API Key IDs, and fail distribution builds when sensitive developer data is embedded.

**Architecture:** `AIHubClient` exposes login and refresh endpoints, while a focused `SessionCoordinator` decides whether to reuse, refresh, or log in. `AppSettingsStore` persists encrypted authentication state and non-sensitive Key selections; `MainForm` requests an authenticated client and never owns token lifecycle rules.

**Tech Stack:** C# 14, .NET 10, WinForms, `HttpClient`, `System.Text.Json`, Windows DPAPI, PowerShell release scripts.

---

## File Structure

- Create `src/AIHubRouter.Core/AuthSession.cs`: login/refresh response models and session expiry data.
- Create `src/AIHubRouter.Core/SessionCoordinator.cs`: refresh-first decision logic with one fallback login.
- Modify `src/AIHubRouter.Core/AIHubClient.cs`: expose login and refresh calls and injectable HTTP transport for real-response tests.
- Modify `src/AIHubRouter.Core/AppSettingsStore.cs`: encrypt email/password/token state and persist selected Key IDs plus selection-initialized state.
- Modify `src/AIHubRouter.WinForms/MainForm.Layout.cs`: add email/password controls and collapse manual headers into an advanced section.
- Modify `src/AIHubRouter.WinForms/MainForm.cs`: integrate session coordinator and selected-Key persistence.
- Modify `tests/AIHubRouter.Core.Tests/Program.cs`: add deterministic session and persistence tests.
- Create `scripts/scan-release.ps1`: scan publish inputs and outputs for sensitive values.
- Modify `scripts/publish.ps1`: enforce scan before reporting successful artifacts.
- Modify `README.md`: document automatic login, expiry behavior, Key selection restore, and clean distribution.

### Task 1: Session decision model

**Files:**
- Create: `src/AIHubRouter.Core/AuthSession.cs`
- Create: `src/AIHubRouter.Core/SessionCoordinator.cs`
- Test: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write the failing reuse test**

Add a test constructing a session expiring in ten minutes and assert `SessionCoordinator.GetSessionAsync` returns it without invoking refresh or login delegates.

- [ ] **Step 2: Run the test and verify RED**

Run `dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj --no-restore -c Release`.
Expected: compilation fails because `AuthSession` and `SessionCoordinator` do not exist.

- [ ] **Step 3: Implement the minimal session types**

Define `AuthSession` with `AccessToken`, `RefreshToken`, and `ExpiresAt`, plus `IsUsable(DateTimeOffset now, TimeSpan margin)`. Define `SessionCoordinator` with injected refresh, login, and session-persist delegates.

- [ ] **Step 4: Run tests and verify GREEN**

Run the same test command. Expected: the reuse test and existing tests pass.

### Task 2: Refresh-first and login fallback

**Files:**
- Modify: `src/AIHubRouter.Core/SessionCoordinator.cs`
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing refresh and fallback tests**

Add one test asserting an expired access token with a refresh token calls refresh but not login, and one asserting a refresh exception calls login exactly once and persists the returned token pair.

- [ ] **Step 2: Run tests and verify RED**

Expected: tests fail because coordinator does not perform refresh or fallback.

- [ ] **Step 3: Implement refresh-first behavior**

Reuse access tokens with a two-minute margin, refresh when possible, catch only authentication failures for fallback, then login with non-empty email/password. Persist every newly issued session before returning it.

- [ ] **Step 4: Run tests and verify GREEN**

Expected: reuse, refresh, fallback, and existing tests pass without warnings.

### Task 3: AIHub login and refresh endpoints

**Files:**
- Modify: `src/AIHubRouter.Core/AIHubClient.cs`
- Modify: `src/AIHubRouter.Core/Models.cs`
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing JSON contract tests**

Use an in-memory `HttpMessageHandler` to return real AIHub envelope shapes for `/auth/login` and `/auth/refresh`. Assert tokens and `expires_in` map to an absolute `ExpiresAt`; assert `requires_2fa=true` produces a dedicated unsupported-interactive-auth error.

- [ ] **Step 2: Run tests and verify RED**

Expected: compilation fails because login/refresh APIs and injectable handler constructor are absent.

- [ ] **Step 3: Implement endpoint methods**

Add `LoginAsync(email, password)` posting `{ email, password }` and `RefreshSessionAsync(refreshToken)` posting `{ refresh_token }`. Keep response bodies out of thrown error messages when they may contain authentication fields.

- [ ] **Step 4: Run tests and verify GREEN**

Expected: contract and existing tests pass.

### Task 4: Encrypted credentials and Key selection persistence

**Files:**
- Modify: `src/AIHubRouter.Core/AppSettingsStore.cs`
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs`

- [ ] **Step 1: Write failing persistence tests**

Extend the DPAPI roundtrip test with email, password, access token, refresh token, expiration, selected Key IDs, and `KeySelectionInitialized`. Assert encrypted bytes contain none of the synthetic secrets. Add an empty-selection roundtrip assertion.

- [ ] **Step 2: Run tests and verify RED**

Expected: compilation fails because the new properties are absent.

- [ ] **Step 3: Extend persistence models**

Store sensitive session/login fields only in `PersistentCredentials`. Store `long[] SelectedKeyIds` and `bool KeySelectionInitialized` in `PersistentAppSettings`, preserving backward compatibility with older JSON files.

- [ ] **Step 4: Run tests and verify GREEN**

Expected: encrypted roundtrip and empty-selection tests pass.

### Task 5: WinForms automatic authentication

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Modify: `src/AIHubRouter.WinForms/AuthGuideDialog.cs`

- [ ] **Step 1: Add account controls**

Add masked password and email fields as the primary flow. Keep Token/Cookie/UA inputs under an “高级认证” expander. Update guidance to state that refresh happens automatically.

- [ ] **Step 2: Integrate the coordinator**

On private API operations, obtain a valid `AuthSession`, construct `AIHubClient` with its access token, and save rotated tokens through `AppSettingsStore`. Manual token remains a fallback when email/password are empty.

- [ ] **Step 3: Handle interactive auth requirements**

Display a bounded error for CAPTCHA/TOTP responses and stop the current routing cycle without retry loops.

- [ ] **Step 4: Build and inspect warnings**

Run `dotnet build AIHubRouter.sln --no-restore -c Release`. Expected: zero warnings and zero errors.

### Task 6: Persist Key selections

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Modify: `src/AIHubRouter.WinForms/MainForm.Layout.cs`

- [ ] **Step 1: Restore selection state**

When `KeySelectionInitialized` is false, select the first active Key once. Otherwise intersect saved IDs with loaded Keys, including a valid empty result.

- [ ] **Step 2: Persist checkbox edits**

Handle `CellValueChanged` for the route checkbox, commit the edit, update the saved ID set, and save immediately when常态化 is enabled.

- [ ] **Step 3: Verify runtime behavior**

Launch the app, select multiple Keys, save, restart, and confirm the same IDs are selected. Uncheck all, save, restart, and confirm none are selected.

### Task 7: Distribution security gate

**Files:**
- Create: `scripts/scan-release.ps1`
- Modify: `scripts/publish.ps1`
- Modify: `src/AIHubRouter.WinForms/AIHubRouter.WinForms.csproj`
- Modify: `README.md`

- [ ] **Step 1: Write the failing scanner fixture**

Run the scanner against a temporary text file containing a synthetic JWT and `C:\Users\DistributionTest`; expected exit code is non-zero. Run against a clean fixture; expected exit code is zero.

- [ ] **Step 2: Implement the scanner**

Scan source and official EXEs for credential-shaped values and absolute user paths. Print only the rule name and file path, never the matched secret.

- [ ] **Step 3: Harden release metadata**

Enable deterministic builds and map the workspace root to `/_/` while retaining `DebugType=None` and `DebugSymbols=false` in publish.

- [ ] **Step 4: Gate publishing**

Invoke the scanner after both artifacts are produced; abort before success output on any match.

- [ ] **Step 5: Publish and verify**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1`. Expected: all tests pass, both EXEs start, and the release scan reports clean.

