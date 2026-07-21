# Authentication Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore actionable, credential-safe login diagnostics for current AIHub authentication responses without changing the Win32-native application architecture.

**Architecture:** Add one Core presentation boundary that converts known exception types and authentication HTTP statuses into fixed safe messages. WinForms and the background routing service consume that boundary instead of trusting exception messages, while `AIHubClient` continues to retain only status and structured code metadata.

**Tech Stack:** C# 14, .NET 10, native WinForms, existing console test harness

---

### Task 1: Lock the authentication diagnostics contract

**Files:**
- Modify: `tests/AIHubRouter.Core.Tests/Program.cs`

- [x] **Step 1: Register focused tests**

Add test cases for HTTP 401, 403, 429, and 503 authentication failures plus an unknown exception containing synthetic credentials.

- [x] **Step 2: Assert fixed safe messages**

Use `SafeErrorPresentation.GetMessage(exception)` and assert that each HTTP status produces distinct guidance. Combine the returned messages and assert that synthetic email, password, Token, Cookie, User-Agent, and raw server text are absent.

- [x] **Step 3: Run the tests and verify RED**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
```

Expected: build failure because `SafeErrorPresentation` does not exist.

### Task 2: Add the Core presentation boundary

**Files:**
- Create: `src/AIHubRouter.Core/SafeErrorPresentation.cs`

- [x] **Step 1: Implement the minimal status mapping**

Add `public static string GetMessage(Exception exception)` with fixed mappings:

```csharp
AIHubApiException { StatusCode: HttpStatusCode.Unauthorized } => "认证失败：邮箱或密码不正确，或保存的 Token/session 已失效。",
AIHubApiException { StatusCode: HttpStatusCode.Forbidden } => "认证被平台策略拒绝：账号可能已停用，或站点当前仅允许管理员登录。",
AIHubApiException { StatusCode: HttpStatusCode.TooManyRequests } => "登录尝试过于频繁，请等待约 1 分钟后再试。",
AIHubApiException { StatusCode: HttpStatusCode.ServiceUnavailable } => "平台认证服务暂时不可用，请稍后重试。",
```

Also preserve fixed messages for interactive authentication, network failure, timeout, and local validation. Unknown exceptions must return a generic fixed message rather than `exception.Message`.

- [x] **Step 2: Run the tests and verify GREEN**

Run the Core test project and expect all tests to pass.

### Task 3: Use one safe mapping everywhere

**Files:**
- Modify: `src/AIHubRouter.WinForms/MainForm.cs`
- Modify: `src/AIHubRouter.Core/RoutingService.cs`

- [x] **Step 1: Replace WinForms local mapping**

Replace calls to `GetSafeErrorMessage(exception)` with `SafeErrorPresentation.GetMessage(exception)` and remove the local method.

- [x] **Step 2: Replace routing-service local mapping**

Replace its local helper with the same Core method so foreground and unattended routing report the same safe diagnosis.

- [x] **Step 3: Run tests and build**

Run:

```powershell
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj -c Release
dotnet build AIHubRouter.sln -c Release
```

Expected: all tests pass and build completes with zero warnings and zero errors.

### Task 4: Verify distribution safety and publish the fix

**Files:**
- Modify only if required by existing scripts: none expected

- [x] **Step 1: Run existing publish/security gates**

Run the repository's documented Win32 publish and secret-scan commands. Confirm no local settings, credentials, identity strings, or raw authentication responses enter artifacts.

- [x] **Step 2: Review the diff**

Confirm the change does not alter login request fields, retry counts, session persistence, routing decisions, or Win32-only packaging.

- [x] **Step 3: Commit and push**

Commit the focused fix and push `codex/sync-onrightpath-v1.0.3` so PR #3 receives the update.
