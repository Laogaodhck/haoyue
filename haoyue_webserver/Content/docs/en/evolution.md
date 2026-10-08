# Evolution Engine

The evolution engine lets Haoyue learn from its own failures: the runtime journals failure signals into an event log, an aggregator clusters them into defect reports, a reflection turn (an isolated agent turn) drafts skills from those reports, and you review every draft by hand. One principle governs the whole pipeline — **a draft never takes effect automatically**.

## How it works

1. **Defect aggregation (E1)**: the runtime journals tool failures, verification struggles, capability gaps and user thumbs-down into the SQLite event journal; `DefectAggregator` computes a stable fingerprint per (kind, tool, normalized error) and clusters them into defect reports.
2. **Reflection turn (E2)**: the reflection runner reviews defect reports inside an isolated turn, writes skill drafts to `~/.haoyue/labs`, and registers them as pending candidates.
3. **Human gate (E4)**: candidates await review — adopting moves a draft into the live skills directory (picked up by the next scan); rejecting deletes it permanently, and the same defect will not produce another candidate.

Anti self-feeding: fingerprints already present in the decision log (adopted or rejected) are skipped by later reflection passes.

## Using it in Desktop

Open “Settings → Evolution”:

- **Defect signals**: browse the improvement points aggregated from recent events (kind, occurrences, error summary, last seen);
- **Reflect now**: start a reflection turn manually — it runs in the background, the page refreshes on completion and a notice summarizes the outcome;
- **Pending drafts**: review candidate skills one by one and adopt or reject them (rejecting asks for confirmation).

The conversation view shows the same adopt/reject actions as a banner; every reflection — manual, scheduled or unattended — broadcasts an `evolution.reflected` event so the desktop can surface the outcome.

## Auto reflection

Enable **auto reflection** under “Settings → Evolution” and the runtime runs a reflection turn in the background at a fixed interval (6 hours by default; presets of 1 hour / 6 hours / 1 day / 7 days, clamped to 30 minutes–7 days). Changes take effect immediately and persist in the configuration file. Auto and manual reflections share one mutex; a conflicting run is skipped and retried on the next tick.

With auto reflection disabled, reflections only run when triggered manually (or via the `evolution.reflect` IPC / a scheduled task).

## Related IPC methods

| Method | Description |
| --- | --- |
| `evolution.inspect` | Aggregate defect reports (optional `limit`) |
| `evolution.reflect` | Start a reflection turn, returns `{started:true}` immediately |
| `evolution.pending-list` | List pending skill drafts |
| `evolution.decide` | Final human verdict: `decision` is `adopt` or `reject` |
| `evolution.config.get` / `evolution.config.set` | Read or update the auto-reflection configuration |

::: tip
Draft skills live under `~/.haoyue/labs`. Until adopted they never enter the skill scan path and have zero impact on sessions.
:::
