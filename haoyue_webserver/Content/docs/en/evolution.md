# Evolution Engine

The evolution engine lets Haoyue learn from its own failures: the runtime journals failure signals into an event log, an aggregator clusters them into defect reports, a reflection turn (an isolated agent turn) drafts skills from those reports, and you review every draft by hand. One principle governs the whole pipeline — **a draft never takes effect automatically**.

## How it works

1. **Defect aggregation (E1)**: the runtime journals seven kinds of failure signals into the SQLite event journal — repeated tool failures, verification struggles, capability gaps, negative user feedback, provider instability (retries of the same model within a short window), user corrections (several consecutive steers in one session) and cancelled turns (several cancellations in one session). `DefectAggregator` clusters them into defect reports under stable fingerprints and stamps each report with a severity (user feedback is always "high"; the rest escalate with occurrences).
2. **Reflection turn (E2)**: the reflection runner reviews defect reports inside an isolated turn, writes skill drafts to `~/.haoyue/labs`, and registers them as pending candidates.
3. **Human gate (P3)**: candidates await review — adopting moves a draft into the live skills directory (picked up by the next scan), deferring parks it on the review list, and rejecting deletes it permanently; the same defect will not produce another candidate.

Anti self-feeding: fingerprints already present in the decision log (adopted, rejected or deferred) are skipped by later reflection passes, and the reflection turn's own sessions are excluded from aggregation.

## Using it in Desktop

Open “Settings → Evolution”; the workbench has five sections:

- **Health overview**: defect signals distilled into a 0–100 health score (grades 优 / 良 / 中 / 差 — excellent / good / fair / poor), with a 7-day per-day trend and a per-kind distribution; negative feedback and repeated tool failures weigh heaviest;
- **Defect signals**: every report shows kind, a severity badge, occurrences, error summary and last seen;
- **Pending drafts**: expand a draft into a workbench — preview `skill.yaml` / `prompt.txt` / `rationale.md` in tabs, rewrite the prompt before adopting, defer (keep it on the list) or reject it (with confirmation);
- **Reflection history**: each reflection run's trigger (manual / interval / threshold), processing counts and errors;
- **Effectiveness tracking**: adoption rate, per-skill usage counts (measured by `declare_skill` invocations) and whether the originating defect has resurfaced.

The conversation view shows the same adopt/reject actions as a banner; every reflection — manual, scheduled or threshold-triggered — broadcasts an `evolution.reflected` event so the desktop can surface the outcome and refresh.

## Auto reflection: interval and threshold

“Settings → Evolution” offers two coexisting triggers:

- **Interval reflection**: runs a reflection turn in the background at a fixed interval (6 hours by default; presets of 1 hour / 6 hours / 1 day / 7 days, clamped to 30 minutes–7 days);
- **Threshold reflection**: fires early once pending defect signals reach a threshold (5 by default, range 1–50) — useful when failures pile up; two automatic reflections are always at least 30 minutes apart.

Changes take effect immediately and persist in the configuration file. Auto and manual reflections share one mutex; a conflicting run is skipped and retried on the next tick. With both disabled, reflections only run when triggered manually (or via the `evolution.reflect` IPC / a scheduled task).

## CLI: haoyue evolve

Inspection and the human gate also work from the terminal:

| Command | Description |
| --- | --- |
| `haoyue evolve defects` | Aggregate defect reports with the health score and a detail table |
| `haoyue evolve pending` | List pending drafts (with status) |
| `haoyue evolve history` | Reflection run history (`--limit` controls the count) |
| `haoyue evolve stats` | Adoption rate, skill usage and recurrence statistics |
| `haoyue evolve decide <fingerprint> adopt\|reject\|defer` | Final verdict; fingerprints accept unique prefixes, and `adopt` takes `--prompt "…"` to rewrite the prompt before installation |

## Related IPC methods

| Method | Description |
| --- | --- |
| `evolution.inspect` | Aggregate defect reports with health score, 7-day trend and kind/tool distribution (optional `limit`) |
| `evolution.reflect` | Start a reflection turn, returns `{started:true}` immediately |
| `evolution.pending-list` | List pending skill drafts (with status and bundled draft files, truncated when oversized) |
| `evolution.decide` | Final human verdict: `decision` is `adopt` / `reject` / `defer`; `adopt` accepts a `prompt` rewrite |
| `evolution.history` | Reflection run history (`limit` defaults to 20) |
| `evolution.stats` | Effectiveness statistics: adoption rate, skill usage and defect recurrence |
| `evolution.config.get` / `evolution.config.set` | Read or update the auto-reflection configuration (interval and threshold groups) |

::: tip
Draft skills live under `~/.haoyue/labs`. Until adopted they never enter the skill scan path and have zero impact on sessions.
:::
