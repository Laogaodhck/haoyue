# Evolution Engine: Skills That Grow From Failure

Haoyue does more than log errors — it turns them into capabilities for future sessions. The evolution engine watches seven kinds of failure signals as they happen, distills them into skill drafts through a reflection turn, and leaves the final call to you.

## The four-step loop

```text
failure signals → defect aggregation → reflection turn → skill draft → human review
```

- **Signal capture**: repeated tool failures, verification loops, capability gaps, your thumbs-down feedback — plus provider instability (models retrying in bursts), consecutive corrections in a conversation, and repeatedly cancelled turns. All seven are journaled locally;
- **Defect aggregation**: similar signals merge under a stable fingerprint with a severity badge, so a problem seen ten times still occupies a single report line;
- **Reflection turn**: an isolated agent turn reviews the reports and writes "what to do next time" as a skill draft under `~/.haoyue/labs`;
- **Human gate**: drafts always await your verdict — adoption moves them into the live skills directory, deferring keeps them on the review list, and rejection deletes them for good; the same defect will not spawn another candidate.

## Health you can see

The "Evolution" page in Settings visualizes the whole pipeline and distills it into a **0–100 health score**: the more severe and dense the defects, the lower the score. Beside it sit a 7-day per-day trend and a per-kind distribution — see at a glance where the system hurts.

Every reflection run lands in a history view (manual, interval or threshold trigger at a glance), and adopted skills enter **effectiveness tracking**: how often they were actually used and whether their defect has resurfaced. Whether evolution truly solves problems is answered with data.

## Working with it in Desktop

The Evolution page supports one-click "Reflect now", severity-ranked defect review, per-file preview of every pending draft (skill.yaml / prompt.txt / rationale.md), **rewriting the prompt right before adoption**, deferring or rejecting drafts. Reflections run in the background and the desktop notifies you when they finish.

Prefer hands-off? Two automatic triggers coexist:

- **Interval reflection**: the runtime reviews signals in the background at your chosen interval (1 hour up to 7 days);
- **Threshold reflection**: fires early once pending defects reach a threshold (5 by default) — useful when failures pile up.

Either way, nothing takes effect without your review.

## Evolution in the terminal too

The `haoyue evolve` command group brings inspection and verdicts to the terminal: `defects` for the health score and detail table, `pending` for drafts awaiting review, `history` for reflection runs, `stats` for adoption rate and skill usage, and `decide` for the final call (fingerprints accept unique prefixes; `adopt` takes `--prompt` to rewrite before installing).

## How it relates to memory

- **Memory (MEMORY.md)** stores facts and conventions injected into every session;
- **Evolution** stores reusable procedures, invoked as skills when needed.

Together they form a local knowledge asset that grows the more you use Haoyue.
