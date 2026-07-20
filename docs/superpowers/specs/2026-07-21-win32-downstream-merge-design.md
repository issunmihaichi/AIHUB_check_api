# AIHubRouter Win32 Downstream Merge Design

## Goal

Merge the routing and usability improvements from
[`OnRightPath/AIHubRouter`](https://github.com/OnRightPath/AIHubRouter) into this
repository while keeping WinForms as the only desktop application and Windows
as the only supported release platform.

## Source And Attribution

The downstream repository is based on this repository and adds three commits:

- `95d2b2d` - initial cross-platform routing service and weighted routing work.
- `0f340e4` - Linux systemd deployment.
- `f7d8afd` - weighted routing stabilization, audit logging, and adaptive themes.

The implementation will port the applicable code rather than fast-forward the
entire downstream branch, because that branch deletes WinForms and replaces it
with Avalonia. The README will contain a visible contribution section linking
to `https://github.com/OnRightPath/AIHubRouter` and naming the features adapted
from it. Commit messages will retain a downstream reference where practical.

## Scope

### Included

- Economy, Balanced, and Speed routing modes.
- Weighted price/first-token-latency evaluation with explainable decisions.
- Route state persistence and account-data caching.
- A shared `RoutingService` used by the WinForms routing workflows.
- Dry-run simulation in the WinForms interface.
- A rotating, credential-free JSONL audit log for dry-run and real cycles.
- Candidate score and decision reason data needed by the UI.
- Follow-system, light, and dark WinForms theme preferences.
- Downstream deterministic tests for weighted routing, invalid values,
  authentication retries, dry-run behavior, and route-state transitions.
- Visible downstream repository attribution in the README.

### Excluded

- Avalonia, Skia, and `AIHubRouter.Desktop`.
- `AIHubRouter.Cli` and all command-line contracts.
- Linux, macOS, ARM64 release matrices, systemd, containers, and shell-based
  cross-platform packaging.
- AES master-key credential storage and Unix file-permission branches.
- Browser automation, proxying model requests, or modifying Codex settings.

The resulting solution remains `AIHubRouter.Core`,
`AIHubRouter.WinForms`, and `AIHubRouter.Core.Tests` only.

## Core Architecture

### Routing Models

`Models.cs` gains the downstream routing domain types:

- `RoutingMode`: `Economy`, `Balanced`, and `Speed`.
- `BalancedRoutingPolicy`: platform, availability threshold, maximum monitor
  age, and mode-specific price/latency weights.
- `RouteEvaluation`: baseline, recommendation, eligible candidates, and score
  inputs.
- `RouteDecision`: current group, target group, switch flag, reason, price
  premium, and latency improvement.
- `RouteState`: the last successfully routed group.

The default mode is `Economy` for backward compatibility with the existing
lowest-price behavior. Users can opt into Balanced or Speed explicitly.

### Routing Engine

`RoutingEngine` keeps `SelectCheapest` for compatibility and adds the
downstream `Evaluate` path. It performs the existing hard filters first, then
uses stable multiplier data as the primary weight and first-token latency as a
secondary weight.

`RouteDecisionEngine` converts an evaluation plus persisted route state into an
explainable switch decision. A recommendation changes immediately when the
weighted score is positive; no cooldown or repeated-confirmation mechanism is
introduced.

### Routing Service

`RoutingService` owns one complete authenticated routing cycle:

1. Reuse, refresh, or recreate the authenticated session through the existing
   refresh-first rules.
2. Fetch public monitor data each cycle.
3. Cache account groups, rates, and Keys for a bounded interval.
4. Resolve the persisted Key selection, including an explicit empty selection.
5. Evaluate candidates and derive an explainable route decision.
6. Return the decision without `PUT` calls in dry-run mode.
7. Update only selected Keys that are not already on the target group.
8. Persist route state only after a successful non-dry-run cycle.

Business API authentication failures are retried once. Network failures never
trigger password login, and server response messages are never reflected into
UI errors.

The service accepts interfaces for the API client and state store so the core
tests remain deterministic and offline.

## Windows Persistence

`PersistentAppSettings` keeps every existing JSON property and adds:

- routing mode;
- account-cache duration;
- WinForms theme preference.

Missing properties deserialize to backward-compatible defaults. Existing
`settings.json` and `credentials.dat` locations remain unchanged.

Sensitive data continues to use current-user Windows DPAPI only. The merge
does not add Linux/macOS credential code or change the encrypted credentials
schema. Route state is non-sensitive and is written atomically to a separate
JSON file in `%LocalAppData%\AIHubRouter`.

## WinForms Integration

The existing WinForms application remains the primary experience and keeps:

- email/password automatic login;
- advanced Token/Cookie/User-Agent fallback;
- encrypted persistence;
- Key checkbox persistence;
- vertical-sync/double-buffer controls;
- provider and Key grids;
- current staging and credential-free release gate.

The routing toolbar adds a compact three-option mode selector and a `模拟`
command. `刷新数据` performs an authenticated dry-run and renders the result;
`模拟` explicitly previews the current decision without writing; `立即路由`
and the timer perform a real cycle.

Provider rows expose effective multiplier, first-token latency, weighted score,
and recommendation state. The status bar reports the decision reason and
whether a dry-run would change any selected Key.

### Native Themes

The Avalonia theme implementation is not copied. Its behavior is reimplemented
with WinForms controls and system colors:

- `跟随系统` reads `AppsUseLightTheme` from the current user's Windows
  personalization registry key and falls back to light when unavailable.
- `浅色` and `深色` apply explicit WinForms palettes.
- grids, toolbars, status surfaces, inputs, and dialogs update together.
- the choice is stored in ordinary settings and contains no credentials.

No Avalonia, Skia, WebView, or browser dependency enters the application or
release package.

### Audit Log

The downstream JSONL audit concept is retained without the CLI. Core exposes a
small writer that records the cycle timestamp, mode, decision reason, target,
candidate scores, dry-run flag, and per-Key success state. It writes under
`%LocalAppData%\AIHubRouter\logs`, rotates by size, and keeps a bounded number
of files. It never records email, password, Token, refresh token, Cookie,
User-Agent, or raw server messages.

## Release Behavior

The existing Windows release scripts remain authoritative:

- compressed self-contained `win-x64` executable;
- framework-dependent lite executable;
- staging-first publication;
- source and binary secret scanning;
- one executable per official artifact directory.

No non-Windows runtime identifiers or platform packages are produced. The
release scanner must continue rejecting credential-shaped values, non-example
email addresses, and local user paths.

## Testing

Core tests will cover:

- all three routing modes and their weights;
- normal and extreme latency/multiplier trade-offs;
- missing, zero, negative, NaN, and infinite measurements;
- explicit empty Key selection;
- dry-run making no update calls;
- one-time business authentication retry;
- route-state transitions and failed-Key behavior;
- audit JSON shape, rotation, and secret-field exclusion;
- existing DPAPI and sensitive-error-message guarantees.

Verification requires:

- all core tests passing;
- `dotnet build AIHubRouter.sln --no-restore -c Release` with no warnings;
- the Windows publish script completing both packages;
- source, DLL, and EXE scans reporting clean;
- both official artifact directories containing only `AIHubRouter.exe`;
- no Avalonia, CLI, Unix deployment, or cross-platform packaging files in the
  final tree.

## Migration And Failure Handling

- Existing users retain their encrypted credentials and selected Key IDs.
- The first run after upgrade defaults to Economy unless a new mode has already
  been saved.
- A missing or unreadable route-state file behaves as no previous route and
  does not invalidate credentials.
- An invalid candidate set stops the cycle without writing any Key.
- Partial Key update failures are reported per Key and invalidate persisted
  route certainty, preventing the next cycle from assuming a successful group.
- Theme detection failures fall back to the explicit light palette.
