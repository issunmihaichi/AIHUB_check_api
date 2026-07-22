# Stable Routing V2 Design

## Goal

Prevent automatic route flapping while preserving immediate recovery from an unavailable, stale, disabled, or missing current route.

## Routing Classes

The decision engine distinguishes two switch classes.

- **Forced recovery:** the observed current group is absent from eligible candidates. This includes unavailable, disabled, stale, unauthorized, or otherwise unroutable groups. Recovery bypasses cooldown and candidate-stability checks.
- **Policy switch:** both current and candidate groups are eligible. This switch must satisfy mode-specific rules plus hysteresis.

## Hysteresis

`RouteState` stores the last automatic policy switch time, completed policy evaluation count since that switch, and the most recently preferred candidate with its consecutive preference count. A policy switch requires both a 30-second dwell period and six completed evaluations since the last policy switch. The candidate must also have remained preferred for two consecutive observations. Forced recovery bypasses the guards for that transition, then resets them so a recovered outage cannot immediately reverse into another automatic switch.

## Mode Semantics

- **Economy:** choose the strict lowest effective multiplier. A frequent call interval never upgrades Economy into Speed.
- **Balanced:** retain the configured countdown, hard 26.73-second deadline, and user soft tolerance. The current route is retained when it satisfies the hard deadline. A cold start chooses the least costly candidate meeting the soft deadline, falling back to the hard deadline. Countdown expiry selects Economy.
- **Speed:** retain the existing 20% generation-speed / 10% price-premium guard, or the 30-second end-to-end gain at no price increase. A candidate without enough observations cannot qualify solely by apparent speed.

## Forecasts And Confidence

The provider window remains keyed by platform and group because AIHub's provider endpoint does not expose a model identifier. It aggregates 20-minute P50 values for display and routing compatibility, and exposes P90 TTFT, P25 output speed, and sample count for conservative deadline evaluation. Candidates with fewer than 20 observations remain eligible but cannot win a speed-driven policy switch.

Balanced mode uses the explicit output budget supplied by settings. When it is missing, the engine records a zero budget instead of inventing a long task; the UI/service can later supply a forecast explicitly. Cost calculations retain the configured context-miss penalty and expose their inputs in the audit record.

## Auditability

Every decision records whether it was forced recovery or policy-driven, the hysteresis state, the target's observation count, and the candidate-performance inputs. This lets a later CSV analysis separate user/manual or outage-induced moves from automatic economic decisions.

## Error Handling

No hysteresis rule may prevent recovery when the current route is invalid. Missing or insufficient performance data may block a speed preference but may not block initial route selection, current-route recovery, or a valid Balanced fallback.
