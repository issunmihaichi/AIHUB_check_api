# Maintainability Refactor Design

## Goal

Reduce the size and responsibility overlap of the WinForms orchestrator and the
custom core-test entry point without changing routing, authentication, persistence,
API, or UI behavior.

## Approaches Considered

1. Split the existing partial `MainForm` and test source by responsibility. This
   preserves private field access, event wiring, and the current test executable
   while making each concern navigable. This is the selected approach.
2. Introduce controller and presenter objects around the form. This creates stronger
   boundaries but would require a UI abstraction and broaden a no-behavior-change
   refactor into a redesign.
3. Rewrite the test project around a third-party test framework. This would improve
   discovery and reporting but adds packaging and workflow changes unrelated to the
   application refactor.

## Scope

### WinForms orchestration

Keep `MainForm` as the single WinForms type. Move existing methods into partial
source files with these ownership boundaries:

- `MainForm.cs`: shared state, constructor, lifetime, and event wiring.
- `MainForm.Authentication.cs`: authentication validation, client/session creation,
  automatic renewals, and credential persistence.
- `MainForm.Routing.cs`: data refresh, routing-service lifecycle, route execution,
  candidate calculation, and grid selection capture.
- `MainForm.Settings.cs`: saved-settings load/save, routing preference conversion,
  countdown state, theme, and smooth-rendering preferences.
- `MainForm.Presentation.cs`: provider/key grid projection, user-facing labels,
  busy/status/error presentation, clipboard helpers, and authentication guide/login
  commands.

`MainForm.Layout.cs` remains responsible only for control creation and visual layout.
Method bodies move without changing their signatures, event registrations, control
field names, messages, or state transitions.

### Core test structure

Keep the current zero-dependency console test executable. Replace the monolithic
top-level test file with:

- `Program.cs`: execute the catalog, report failures, and optionally run the public
  smoke test.
- `TestCatalog.cs`: preserve the current test names and deterministic order.
- Partial `CoreTestCases` source files grouped by credentials, provider metrics,
  adaptive switching, balanced deadlines, route decisions, routing service,
  persistence, and authentication diagnostics.
- `TestFixtures.cs`: shared assertion and data-construction helpers.

The catalog will expose a name/body list and reject duplicate test names before any
test executes. Existing tests remain the behavioral characterization suite; moving
them must not change their names or assertions.

## Non-Goals

- No change to multiplier calculations, deadline policy, 20-minute median behavior,
  API endpoints, request headers, encrypted settings format, or release packaging.
- No third-party framework, dependency, platform, UI redesign, or data migration.
- No release publication as part of this refactor.

## Error Handling And Verification

The runner continues executing every test after individual failures and returns a
nonzero exit code when any fail. The smoke test remains opt-in through
`AIHUB_SMOKE_TEST=1`. Verification must run the full core test project, the Release
solution build, and `git diff --check`.
