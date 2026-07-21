# Adaptive Economic Switch Design

## Goal

Replace the score-advantage switch guard with the supplied token-cost,
context-penalty, completion-time, and call-interval algorithm while preserving
the native WinForms application, the existing provider eligibility filters,
encrypted persistence, Key selection behavior, and Windows-only release.

## Confirmed Inputs

The provider monitor already supplies effective multiplier inputs,
`firstTokenLatencyMs`, and `outputTokensPerSecond`. The supplier interface can
also supply the last user-call time. The client accepts both
`lastCallEndedAt` (preferred) and `lastCallAt` (compatibility), as ISO-8601
timestamps. No credentials or local request contents are needed for the
calculation.

The current interval is calculated from the latest call timestamp in the
current Key group. If the current group is unknown, the latest timestamp from
the selected platform is used. A timestamp up to one minute in the future is
clamped to zero for ordinary clock skew; a more distant future timestamp or a
missing timestamp produces an unknown interval. An unknown interval retains
the user's base preference instead of pretending the user is idle.

## Global Constants

Core owns one `AdaptiveRoutingConstants` class:

- input price: `5.0` USD per million tokens;
- output price: `30.0` USD per million tokens;
- context-miss penalty: `300_000` input tokens;
- generated-token planning rate: `43.6` tokens per second, accounting for
  sub-agent calls consuming roughly twice the token budget;
- minimum useful remaining work: `1_000` tokens;
- maximum completion time in Cost mode: `86_400` seconds.

Duration configuration is immutable:

| Category | `R_min` | `R_max` | Expected time |
| --- | ---: | ---: | ---: |
| Short | 0 | 156,960 | 3,600 s |
| Medium | 156,960 | 627,840 | 7,200 s |
| Long | 627,840 | 3,767,040 | 21,600 s |

The maximum values use the next duration boundary. Long is capped at 24 hours
times 43.6 tokens per second.

## Preferences And Candidate Proposal

Existing UI modes map directly:

- Economy -> Cost;
- Balanced -> Balanced;
- Speed -> Speed.

The effective preference follows the supplied interval boundaries exactly:

- below 5 seconds -> Speed;
- 5 through 15 seconds -> base preference;
- above 15 through 30 seconds -> Balanced only when the base is Speed,
  otherwise Cost;
- above 30 seconds -> Cost.

Provider eligibility remains unchanged: enabled, latest available state,
authorized group, platform, valid public/effective multiplier, freshness, and
the configured 6-hour success threshold are all required.

The existing evaluator still calculates its deterministic score for display
and for initial/invalid-route recovery, but that score is only a reference
once a current route is valid. The decision engine traverses every eligible
candidate, applies the adaptive pairwise guard to each one, discards rejected
candidates, and then selects among accepted candidates deterministically:
Cost/Balanced maximize net saving (then completion time and multiplier), while
Speed minimizes completion time (then generation speed and net saving). This
allows a candidate beyond the weighted winner to be selected when it is the
one that satisfies the supplied algorithm.

For a valid current route, the decision also exposes the ordered accepted
candidates and the rejection reason for every evaluated alternative. WinForms
uses that same result for the provider table's `算法排行` column (`#1`, `#2`,
or `不建议`) and sorts suggestions by the algorithm rank. The legacy weighted
score remains visible as `参考分` for diagnostics only.

## Pairwise Switch Engine

`AdaptiveSwitchDecisionEngine` is a pure Core component. Its request contains
old/new multipliers, TTFT seconds, generation speeds, duration category, base
preference, and optional current interval. It exposes the effective preference,
selected remaining-token estimate, penalty, net saving, old/new completion
times, time delta, a stable reason enum, and a bounded Chinese detail string.

The formulas and comparisons match the supplied pseudocode:

- penalty is `300000 * newMultiplier * 5 / 1_000_000`;
- net saving is remaining output-token saving at 30 USD/M minus penalty;
- completion time is TTFT plus remaining tokens divided by generation speed;
- Cost requires positive net saving and completion below 24 hours;
- Balanced requires net saving above half the penalty, acceptable completion
  time, and a price reduction greater than 5 percent;
- Speed accepts generation speed above 120 percent with price at most 110
  percent, or an end-to-end gain above 30 seconds without a price increase.

Non-finite/negative multipliers are already excluded. Missing, non-positive,
or non-finite generation speed produces infinite completion time and cannot
accidentally satisfy a time condition. Invalid TTFT is handled the same way.

No current route and an invalid current route retain their existing immediate
recovery behavior because there is no safe old/new comparison. An already
optimal route remains unchanged. Otherwise a rejected adaptive decision keeps
the current target and does not update any Key.

## Persistence And WinForms

Add a persisted `TaskDurationCategory` with Medium as the default. The routing
toolbar gets one compact duration combo (`短任务`, `1-4 小时`, `4 小时以上`)
next to the existing mode combo. It follows existing native WinForms layout,
theme, scaling, and persistence patterns. Changing it invalidates the routing
service and recalculates the preview.

The status strip reports the effective preference and the safe decision detail
after a routing cycle. It may show computed money/time values, group IDs, and
the interval, but never credentials, request contents, server error messages,
or provider warning messages.

## Audit And Safety

The JSONL audit records the stable adaptive reason, effective mode, duration,
optional interval, and numeric decision metrics. The detail is generated
locally from numbers only. Existing credential-name and release scans remain
mandatory.

## Trigger Semantics

The Win32 client evaluates whenever it receives a fresh provider summary:
manual refresh, simulation, immediate routing, or the automatic timer. A price
change or last-call timestamp change therefore affects the next observed
cycle. The desktop app does not invent a request proxy or cross-platform
daemon.

## Verification

Core tests cover constants, every interval boundary, duration token selection,
all Cost/Balanced/Speed accept and reject branches, invalid performance data,
last-call JSON aliases, current-group interval resolution, service-level Key
updates, persistence defaults/roundtrip, and credential-free audit output.
Final gates are the full Core executable, warning-free Release build,
`scripts/publish.ps1`, Windows-only file scan, release content scan, and an
independent review before updating PR #2.
