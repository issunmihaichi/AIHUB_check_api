# OnRightPath v1.0.3 Selective Compatibility Design

## Goal

Continue compatibility with
[`OnRightPath/AIHubRouter`](https://github.com/OnRightPath/AIHubRouter) by
adapting the routing-stability and latest-provider-status changes from its
`v1.0.2` and `v1.0.3` releases, while preserving this repository's native
Windows WinForms product decisions.

## Upstream Changes In Scope

The selective sync targets these downstream commits:

- `eed1b72` (`v1.0.2`) - price-first weight tuning and a minimum weighted-score
  advantage before replacing an otherwise valid current route.
- `a3e8380` (`v1.0.3`) - provider warning metadata and explicit reliance on the
  latest `enabled` / `available` provider state for eligibility.

Code is adapted into the existing Core contracts rather than merged wholesale.
This avoids importing the downstream CLI, Avalonia desktop project, Linux
credential behavior, systemd files, or cross-platform release surface.

## Preserved WinForms Behavior

The following local behavior remains authoritative:

- `AIHubRouter.WinForms` is the only application.
- Windows x64 self-contained and lite packages are the only releases.
- `Economy` remains the default routing mode for backward compatibility.
- Existing weights remain Economy 95/5, Balanced 80/20, and Speed 35/65.
- The user-visible 6-hour availability threshold remains configurable and is
  still a hard eligibility filter.
- Email/password refresh-first authentication, DPAPI persistence,
  Token/Cookie/User-Agent fallback, selected-Key persistence, dry-run, audit,
  themes, and vertical sync remain unchanged.

## Provider Warning Compatibility

`ProviderStatus` gains the downstream `warningReasons` JSON contract and a
derived `HasWarnings` flag. Warning items contain `type`, `message`, and an
optional `count` so current and future downstream responses deserialize without
discarding their shape.

Warnings do not make a provider ineligible by themselves. Eligibility continues
to require the latest observation to be enabled, available, fresh, authorized,
finite, and above the configured 6-hour success threshold.

The WinForms provider table marks an otherwise routable warning entry as
`可路由（警告）`. It never displays or logs the server-provided warning message,
because arbitrary server text may contain account or request details. Audit
entries keep their existing explicit credential-free shape.

## Stable Route Decisions

`BalancedRoutingPolicy` gains an optional minimum score-advantage override and
a default threshold of `0.05`, matching the downstream stability concept. The
threshold is applied only when:

- the observed/persisted current group is still an eligible candidate;
- a different weighted candidate is recommended; and
- both current and target scores can be calculated from finite positive price
  and latency measurements.

If the target score leads by `0.05` or less, the decision keeps the current
group, sets `ShouldSwitch` to false, and reports
`RouteDecisionReason.ScoreAdvantageTooSmall`. A lead strictly greater than
`0.05` switches immediately.

The threshold never blocks an initial route, an invalid current route, a
zero-price baseline, or a decision where either weighted score is undefined.
Mixed selected-Key groups still reconcile to the chosen target through the
existing `RoutingService` behavior.

## User Interface

No new tuning control is added. The stability threshold is an internal policy
default so the compact native toolbar does not become more complex. The status
bar maps `ScoreAdvantageTooSmall` to `优势较小，保持当前路由`.

Provider warning state appears in the existing status column. The public rate,
effective rate, first-token latency, weighted score, recommendation, and
configured 6-hour threshold remain visible and behave as before.

## Testing

Core tests cover:

- `warningReasons` JSON deserialization and `HasWarnings`;
- a warning provider remaining eligible when its latest state is available;
- the latest unavailable state remaining ineligible even with strong historical
  success data;
- the local 6-hour threshold still excluding a low-success provider;
- close faster and close cheaper recommendations keeping the current group;
- a score advantage above `0.05` switching immediately;
- initial, invalid-current, zero-price, unknown-latency, and mixed-Key behavior
  remaining compatible;
- settings deserialization retaining the selected local weights and default
  Economy mode.

Final verification runs the complete Core test executable, a warning-free
Release build, the Windows-only file check, and the staging-first publish and
security scans.

## Attribution

The existing README attribution to `OnRightPath/AIHubRouter` remains visible.
The compatibility implementation references downstream `v1.0.2` and `v1.0.3`
in documentation and commit history without claiming support for downstream
platforms excluded above.
