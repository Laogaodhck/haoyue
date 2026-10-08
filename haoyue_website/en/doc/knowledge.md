# Knowledge Base: Experience You Can Search

If memory (MEMORY.md) is the working memo injected into every conversation, the knowledge base is the archive you consult on demand. The agent saves facts, commands, pitfalls and fixes as you work, then retrieves them semantically when a related question comes up — and you can maintain the same store by hand in the desktop client or the CLI.

## Why substring matching wins here

Most retrieval stacks lean on tokenizers, and SQLite's tokenizers simply do not understand Chinese. Haoyue takes a pragmatic route: normalize the query (FormKC so full-width equals half-width), slice Chinese into bigrams so unspaced sentences still hit, expand synonyms, allow small typos, then score by substring hits — title 3, content 2, tags 2 — ranked coverage-first. Mixed Chinese/English queries reliably outperform what a borrowed English tokenizer would deliver.

## What's new in this release

- **Tag system**: tags match as whole words (`build` never matches `buildtool`, full-width commas split correctly), `knowledge.tags` aggregates counts, and both listing and search accept a tag filter;
- **One-click export**: the whole scope renders into a single Markdown document with scope, timestamp and tags — the desktop saves through the system dialog, the CLI writes a file or prints to stdout;
- **Synonym editor**: `~/.haoyue/knowledge/synonyms.txt` has always been the hidden tuning hook; the desktop can now read and write it directly. One line per group (`key = synonym1, synonym2`), hot-reloaded on save, merged over the built-in technical table;
- **Editing never forks**: renaming an entry used to silently create a duplicate; edits now save in place by id;
- **CLI import/export**: `haoyue knowledge import` batch-imports files (per-file reporting, failures don't stop the batch), `haoyue knowledge export` backs up everything in one command.

## Where to find it

The desktop "Knowledge" page puts all of it on one screen: the tag panel for exact filtering, a download button for export, and a slider button for the synonym editor; `Ctrl+N` adds, `Ctrl+F` focuses search, `Esc` backs out layer by layer. On the CLI, the `knowledge` command group covers list / search / add / show / delete / import / export.

Agent tools and the UI share one store: entries you add by hand are visible to the agent on its next retrieval, and everything the agent saves is ready for you to browse, revise or export.
