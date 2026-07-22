# Routing Stability V2

## Switch Classes

- `Initial`: no current route exists; select an eligible route.
- `ForcedRecovery`: the observed current group is no longer eligible. This covers unavailable, disabled, stale, unauthorized, or missing groups. Recovery selects an eligible route immediately and never waits for cooldown.
- `Policy`: both current and target groups are eligible. This is an economic, balanced, or speed preference change and must pass hysteresis.

## Policy Hysteresis

After an automatic policy or recovery transition, the router requires all of the following before another policy transition:

1. At least 30 seconds have elapsed.
2. At least six completed routing evaluations have occurred.
3. The same target has been preferred on two consecutive evaluations.

The state is persisted in `route-state.json`. Existing installations without the new state fields may perform one normal migration switch; later automatic transitions use the full guard.

## Mode Rules

- Economy remains Economy even when calls are less than five seconds apart. It chooses the lowest effective multiplier among eligible groups.
- Balanced retains the countdown and user soft tolerance. Completion estimates use `P90(TTFT) + output budget / P25(output rate)`. A current route meeting the hard deadline is retained. When it cannot meet the hard deadline, the lowest-cost feasible route is selected immediately.
- Speed retains the 20 percent generation-speed improvement with a 10 percent price cap, or a 30-second end-to-end gain at no price increase. A node with 1-19 locally observed performance samples cannot win solely because it appears faster.

## Performance Window

The existing 20-minute window is keyed by `platform + GroupId`. It provides P50 values for normal presentation, plus P90 first-token latency, P25 output rate, and a conservative sample count for routing. AIHub's provider endpoint has no model identifier, so the router does not claim model-level measurements.

## Audit Record

`routing.jsonl` records the switch class, hysteresis state, P90/P25 performance inputs, sample count, deadline calculations, and adaptive cost figures. It deliberately avoids credential values and credential-like field names.

## Limits

The provider endpoint does not supply per-request output token usage or a model identifier. Session-residual output forecasting therefore cannot be inferred reliably inside the router yet; Balanced mode uses the explicit expected output budget configured by the user.
