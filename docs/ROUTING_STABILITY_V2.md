# Routing Stability V2

## Switch Classes

- `Initial`: no current route exists; select an eligible route.
- `ForcedRecovery`: the observed current group is no longer eligible because of a hard gate, such as latest unavailable/disabled state, missing authorization or group, blocklist, invalid rate, the success threshold, or a fresh active-probe failure. Recovery selects an eligible route immediately and never waits for hysteresis. Stale but usable evidence alone is not forced recovery.
- `Policy`: both current and target groups are eligible. Every normal Economy, Balanced, or Speed change is a Policy switch, including Economy downshifts and Balanced Deadline/fastest-fallback changes, and must pass hysteresis.
- `ManualOverride`: the user pinned a routable group from the provider context menu. It overrides mode policy and hysteresis until that group becomes ineligible.

## Policy Hysteresis

Every normal Policy transition with an initialized hysteresis baseline requires all of the following:

1. At least 30 seconds have elapsed.
2. At least six completed routing evaluations are already recorded in state.
3. Two observations of the same target are already recorded in state.

The current proposal is evaluated against the previously recorded state. The first two consecutive proposals for a new target record observations one and two; the next proposal for that same target can switch, so stable switching normally occurs on the third proposal. A different target or no proposed switch resets the pending-target observations.

`Initial`, `ForcedRecovery`, and `ManualOverride` execute immediately without these gates and establish a dwell baseline. Policy, recovery, and manual-override transitions reset the dwell/evaluation/stability counters for later Policy changes. Releasing a forced group clears pending observations but preserves an existing baseline.

The state is persisted in `route-state.json`. Existing installations whose old file has a current group but no `LastPolicySwitchAt` may perform one normal migration switch; that switch establishes the baseline, and later automatic transitions use the full guard.

The same state file stores the optional forced `GroupId`. Once authenticated account data confirms that the group fails a hard eligibility rule, preview, simulation, or a real routing cycle clears the pin before normal recovery. Simulation still never writes a Key. This does not alter provider multipliers or measurements.

## Mode Rules

- Economy always remains strict Economy and chooses the lowest effective multiplier among eligible groups. Moving to a newly cheaper eligible group is a normal Policy switch and uses hysteresis.
- Balanced has no countdown. It always uses the explicit output budget (default 1,000), the 26.73-second hard deadline, and the configured user soft tolerance. Completion estimates use `P90(TTFT) + output budget / P25(output rate)`. During a continuous call, a current route meeting the hard deadline is retained; otherwise the lowest-cost route within the effective deadline is proposed. If none can meet it, the fastest alternative is proposed. Both normal switch outcomes use Policy hysteresis.
- Speed retains the 20 percent generation-speed improvement with a 10 percent price cap, or a 30-second end-to-end gain at no price increase. A node with 1-19 locally observed performance samples cannot win solely because it appears faster.
- Last-call interval overrides apply only when the user selected Speed: it remains Speed through 15 seconds, resolves to Balanced above 15 through 30 seconds, and resolves to Cost above 30 seconds. Invalid or absent interval evidence leaves it at Speed. User-selected Economy and Balanced are never interval-overridden.
- Task size (`Short`, `Medium`, `Long`) is independent of Balanced output budget and is used only for the legacy adaptive remaining-token estimates reached from user-selected Speed.

## Performance Window

The 30-minute window is keyed by `platform + GroupId`. Numeric provider metrics use P50/median values for normal presentation, plus P90 first-token latency, P25 output rate, and a conservative sample count for routing. Latest `Enabled`, `Available`, and `CheckedAt` are current state and are never historical boolean/timestamp medians. AIHub's provider endpoint has no model identifier, so the router does not claim model-level measurements.

Usable evidence can come from `CheckedAt`, a fresh active-probe success, finite positive TTFT, or finite positive output speed. Evidence older than 30 minutes remains eligible with weight `max(0.25, 30 minutes / age)`; the boundary at 30 minutes has weight `1`. Valid performance without any timestamp receives weight `0.25`. Weight reduces only positive speed and reliability benefit, never price premium or a negative speed/reliability penalty.

Latest unavailable/disabled state, unauthorized or blocked groups, invalid rates, normalized `SuccessRate6h` below the configured threshold, and fresh active-probe failures remain hard gates. The ranking score is:

```text
latencyWeight * weightedSpeedup
- priceWeight * premium
+ 0.15 * weightedPositiveReliabilityDelta
```

`SuccessRate6h` is normalized to `0..1`. Positive speedup and reliability delta are multiplied by evidence weight; zero or negative values are not reduced.

## Health Check Evidence

At each configured interval (default 90 seconds), the scheduled selected-Key check reads only that Key's current group and probes it without moving the Key. It records a success or failure under the exact `platform + GroupId`, so the result participates in routing evidence. The TTL is `min(2 * interval, 30 minutes)`, inclusive; the default is 180 seconds. Expired successes and failures are neutral. While enabled, the health Key is excluded from business-Key routing.

## Live Target Preflight

Live preflight runs only during a real route cycle in which at least one business Key would be written. A fresh successful observation for the exact target `platform + GroupId` skips it. Otherwise the router:

1. Temporarily moves the dedicated health Key to the target and confirms the update response.
2. Sends the streaming ping and requires a content token.
3. Restores the health Key in a `CancellationToken.None` finally path and confirms the restore response.
4. Starts business-Key writes only after successful restoration and target validation.

A recoverable target failure records a failed observation, excludes that group for this cycle, and re-evaluates from the cycle's original route state. If every target fails, or restoration is uncertain, no business Keys are written. Dry-run, no-write, and no-target paths have zero live-probe side effects.

Transport failures without a status, HTTP 408/429/5xx, and ordinary non-global protocol failures can represent a target failure. Local configuration errors, AIHub account/control-plane errors, caller cancellation, upstream 401/403, and safely recognized global codes (`invalid_api_key`, `authentication_error`, `unauthorized`, `forbidden`, `model_not_found`, `invalid_model`) abort without poisoning node health.

HTTP 200 JSON, `+json`, and SSE business errors or malformed data cannot become success. Envelope error fields take precedence; successful envelopes unwrap `data`, `result`, or `payload`, while direct business objects remain supported. Diagnostics never echo response bodies or credentials and do not misreport an application-level failure as an HTTP 200 transport failure.

## Audit Record

`routing.jsonl` records the switch class, hysteresis state, P90/P25 performance inputs, sample count, deadline calculations, and adaptive cost figures. It deliberately avoids credential values and credential-like field names.

## Limits

The provider endpoint does not supply per-request output token usage or a model identifier. Session-residual output forecasting therefore cannot be inferred reliably inside the router yet; Balanced mode uses the explicit expected output budget configured by the user.
