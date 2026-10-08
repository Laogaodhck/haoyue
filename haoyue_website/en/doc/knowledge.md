# Knowledge Center: Notebooks, Sources and Searchable Experience

If memory (MEMORY.md) is the working memo injected into every conversation, the knowledge center is the archive you consult on demand — and this release turns that archive into a real knowledge management system: a **notebooks → sources → entries** hierarchy that gives every fact a place and an origin.

## The three layers

- **Notebooks** are the grouping dimension. Every workspace ships with a permanent default notebook where agent-saved knowledge lands; you can create more by project or topic, keeping "frontend pitfalls", "deployment runbooks" and "meeting notes" separate.
- **Sources** are where knowledge comes from. A pasted snippet, a web URL or a local file — each is chunked into entries on import and remembers its locator (path / URL). URL sources fetch and strip the page down to text; file sources cover txt / md / docx / xlsx and most code formats.
- **Entries** are the retrieval unit. Every one carries a notebook + source attribution badge, so the "all entries" view always tells you where a fact came from.

Deleting a source removes every entry derived from it, and sources refresh in one click — re-read the file or re-fetch the page and the chunks rebuild in place, so re-imports never duplicate.

## Retrieval: across notebooks, too

Search keeps Haoyue's pragmatic substring pipeline: FormKC normalization (full-width equals half-width), Chinese bigram slicing, synonym expansion, small-typo tolerance, then scoring — title 3, content 2, tags 2. SQLite's tokenizers do not understand Chinese, and this route reliably hits where borrowed tokenizers miss.

The new `knowledge.retrieve` is the **cross-notebook search**: one query spans multiple notebooks (or an explicit set of sources), merged and ranked by relevance — "I know I saved it, just not where" is no longer a problem. Tag filtering (exact whole-tag match) works in listing, per-notebook search and cross-notebook search alike.

## Desktop: a three-zone layout

The left sidebar holds notebooks (default badge, entry counts, hover to rename or delete); the top-right panel manages sources (file import, URL source, text source; expand a row to preview its text); below it, the entry list shows attribution badges on every row. Search runs as you type and scopes itself to the selected notebook; the tag panel filters with one click; the export button renders the scope as a Markdown backup; the synonym editor hot-reloads on save. `Ctrl+N` adds, `Ctrl+F` focuses search, `Esc` backs out layer by layer.

On the CLI, the `haoyue knowledge` command group covers list / search / add / show / delete / import / export.

Agent tools and the UI share one store: entries you add by hand are visible to the agent on its next retrieval, and everything the agent saves is ready for you to browse, revise or export.
