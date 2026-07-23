# Routing Reliability Hardening Design

**Date:** 2026-07-24

## Goal

Remove the misleading Balanced countdown and make route selection resilient to
provider-reporting errors by adding policy hysteresis, target health preflight,
robust HTTP-200 envelope handling, a 30-minute metrics window, and useful but
deprioritized stale evidence.

## Scope and Invariants

- The application remains WinForms/Win32 native. No browser runtime or service
  is introduced.
- A selected health-check Key is a temporary probe credential only and remains
  excluded from ordinary business-Key routing.
- `Enabled`, `Available`, platform/group authorization, blocklist, valid price,
  and the user-configured six-hour success threshold remain hard eligibility
  requirements. Historical observations must never revive a latest explicit
  `Enabled=false` or `Available=false` state.
- Dry-run/simulation is side-effect free: it may read cached probe observations,
  but never moves the health-check Key or writes a business Key.
- Existing forced recovery and explicit forced-group behavior remain immediate;
  ordinary policy changes are debounced.

## Routing Changes

### Countdown removal

Remove the Balanced countdown controls, timer, persisted end timestamp, and
zero-expiry Economy transition. Balanced always uses the explicit expected
output-token budget, the 26.73-second hard deadline, and the configured soft
tolerance. Retain task-size (`Short`, `Medium`, `Long`) as an independent
setting for the legacy adaptive Speed calculations; it is no longer derived
from a clock. Unknown legacy countdown JSON properties are ignored by
`System.Text.Json`, so the next save naturally migrates old settings.

### Hysteresis

When Balanced Deadline proposes a normal switch and a current route exists, pass
the proposal through the existing policy hysteresis gates: 30 seconds of dwell,
six completed evaluations, and two stable target observations. Initial routing,
forced-group selection, and current-route recovery bypass those gates. A
deadline fallback is treated as a normal policy switch and is debounced too.

### Active health preflight

When active probing is enabled, a real route cycle validates the proposed target
before changing business Keys:

1. Temporarily move the dedicated probe Key to the proposed group.
2. Send the configured streaming `ping` request and require a content token.
3. Restore the probe Key in a non-cancellable `finally` path.
4. On success, record a timestamped latency observation and continue.
5. On a probe failure, mark that group failed for this cycle, re-evaluate the
   remaining candidates, and try the next target.
6. If restoration fails, abort the cycle and do not modify business Keys.

Probe success/failure observations are cached for twice the configured probe
interval (capped at 30 minutes). A fresh failure is a temporary hard gate; an
expired or absent probe is neutral and falls back to provider data. Simulation
only consumes the cached state.

## HTTP/JSON Handling

The shared client will classify the JSON body before deserializing it, even when
the HTTP status is 2xx. Error signals have priority over success signals:

- `success: false`;
- a non-empty `error` object/string;
- a non-success `code` or `status`.

Success codes include numeric/string `0`, `2xx`, `OK`, and `SUCCESS`. Supported
success envelopes unwrap `data`, `result`, or `payload`; direct business JSON
(`MonitorSummary`, `GroupInfo`, etc.) remains supported. A wrapper that claims
success but contains no usable payload is a format error. Error codes are
retained in `AIHubApiException` without reflecting server credential text.
`JsonElement` payloads are cloned/deserialized before the document is disposed.

The streaming probe applies the same business-error detection to an HTTP-200
JSON body and reports a structured failed observation instead of a generic
missing-token message.

## Metrics and Ranking

- `ProviderMetricsRollingWindow.DefaultWindow` becomes 30 minutes.
- Routing status freshness becomes 30 minutes.
- Latest `Available`, `Enabled`, and `CheckedAt` values are used as current
  state; historical boolean medians cannot mask a current failure.
- A candidate with valid evidence older than 30 minutes remains eligible and
  visible. Its evidence weight is `1` through 30 minutes, then
  `max(0.25, 30 minutes / age)`. The weight reduces positive latency/reliability
  benefit, never the price penalty, so stale cheap nodes do not become cheaper
  merely because their data is old.
- Candidates with neither a usable performance observation nor a usable check
  timestamp remain ineligible.
- Six-hour success rate contributes a bounded reliability term to ranking in
  addition to the existing threshold and tie-break behavior. Define
  `ReliabilityWeight = 0.15` and add
  `ReliabilityWeight * (candidateSuccess6h - baselineSuccess6h)` to the
  existing tradeoff score. Missing success data contributes zero after the
  existing threshold filter; this term cannot override hard availability or
  probe failures.
- For a stale candidate, multiply only positive latency and reliability
  benefits by its evidence weight. Keep the existing price penalty unchanged.
  This avoids making old low-price data artificially attractive while still
  allowing it to remain useful.
- Presentation and routing use the same freshness/probe gate. Stale eligible
  rows are shown as `stale data (deprioritized)`, not as unavailable.

## Compatibility and Migration

Old settings containing countdown fields load without failure and are migrated
on the next save. The obsolete `BalancedCountdownExpired` audit enum value may
remain for log compatibility but is no longer emitted. Existing route-state
files retain their policy-hysteresis counters.

## Verification Plan

Add failing core tests before implementation for:

- Balanced always entering Deadline without countdown state;
- normal Balanced switch hysteresis, including the existing third-observation
  semantics;
- probe success, fresh failure fallback, probe expiry, cancellation, and
  restoration failure safety;
- HTTP-200 success/error envelopes, direct JSON, malformed/empty payloads, and
  authentication fallback;
- 30-minute rolling-window boundaries and latest availability hard gates;
- stale evidence weighting, ranking order, and presentation parity;
- six-hour reliability affecting ranking without bypassing hard filters.

Run the full Core test catalog, build the WinForms Release target, and inspect
the generated diff for credentials and obsolete countdown references.
