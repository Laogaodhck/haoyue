# Evolution Engine: Skills That Grow From Failure

Haoyue does more than log errors — it turns them into capabilities for future sessions. The evolution engine watches failure signals as they happen, distills them into skill drafts through a reflection turn, and leaves the final call to you.

## The four-step loop

```text
failure signals → defect aggregation → reflection turn → skill draft → human review
```

- **Signal capture**: repeated tool failures, verification loops, capability gaps (attempts to load missing skills) and your thumbs-down feedback are all journaled locally;
- **Defect aggregation**: similar signals merge under a stable fingerprint, so a problem seen ten times still occupies a single report line;
- **Reflection turn**: an isolated agent turn reviews the reports and writes "what to do next time" as a skill draft under `~/.haoyue/labs`;
- **Human gate**: drafts always await your verdict — adoption moves them into the live skills directory, rejection deletes them for good and the same defect will not spawn another candidate.

## Working with it in Desktop

The "Evolution" page in Settings visualizes the whole pipeline: defect signals at a glance, a one-click "Reflect now" action, and adopt/reject buttons on every pending draft. Reflections run in the background and the desktop notifies you when they finish.

Prefer hands-off? Flip on auto reflection and the runtime reviews signals in the background at your chosen interval (1 hour up to 7 days). Either way, nothing takes effect without your review.

## How it relates to memory

- **Memory (MEMORY.md)** stores facts and conventions injected into every session;
- **Evolution** stores reusable procedures, invoked as skills when needed.

Together they form a local knowledge asset that grows the more you use Haoyue.
