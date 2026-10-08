# Knowledge Center

The knowledge center is Haoyue's long-term fact store, organized as **notebooks → sources → entries**:

- **Notebooks** are the grouping dimension — every scope ships with a permanent default notebook, and you can create more by project, topic or team;
- **Sources** are where knowledge comes from — a pasted text, a fetched web page or a local file; each one is chunked into entries on import and remembers its origin;
- **Entries** are the retrieval unit — every entry carries a notebook/source attribution badge. The agent saves knowledge through the `knowledge_save` / `knowledge_search` / `knowledge_forget` tools, and you can maintain the same store in the Desktop or the CLI.

Entries are isolated per scope — the current workspace or global — persisted in SQLite, and survive restarts and upgrades.

## How retrieval works

Knowledge search deliberately uses **substring matching** instead of full-text indexing: SQLite's tokenizers do not segment CJK text, and substring matching is what makes mixed Chinese/English queries actually hit. The server applies a full preprocessing pipeline to every query:

- **Normalization**: Unicode FormKC folding + lowercasing, so full-width input equals half-width;
- **CJK bigrams**: Chinese queries are sliced into two-character pieces, letting unspaced questions hit;
- **Synonym expansion**: query terms are expanded through the synonym table (see below);
- **Typos**: edit distance ≤ 1 for short words, ≤ 2 for long ones (applied to titles and tags);
- **Scoring**: title +3, content +2, tags +2; ranked by coverage first, then score, then recency.

There are two retrieval endpoints: `knowledge.search` searches inside one notebook, and `knowledge.retrieve` is the **cross-notebook search** — one query spans multiple notebooks (or an explicit set of sources), merged and ranked by relevance, for those "I know I saved it somewhere" moments. Both accept a `tag` filter.

## Notebooks and sources

**Notebooks** (`knowledge.notebook.*`):

- every scope has exactly one default notebook, which cannot be deleted; deleting another notebook removes all of its sources and entries with it (with a confirmation prompt);
- notebooks carry a name, description and live counts (entries / sources).

**Sources** (`knowledge.source.*`) come in three kinds:

- **text**: paste a snippet directly — title + body + tags, chunked at 6000 characters;
- **url**: give a URL and the server fetches the page text (HTML stripped, ~200k chars per page cap, tagged `web,<host>` by default);
- **file**: txt / md / csv / code files (UTF-8 with GBK fallback), docx and xlsx; 10 MB per file, chunked at 6000 characters, up to 200 entries per file.

Every source remembers its **locator** (file path / URL), and the source panel can **refresh** it at any time — re-reading the file or re-fetching the page rebuilds the chunks in place (same-title upsert, never duplicates). Deleting a source deletes every entry derived from it.

## Tags: exact grouping

Tags are comma-separated (full-width `，` `；` accepted too) and match by **whole-tag equality** (case-insensitive) — the tag `build` does not match `buildtool`. That is deliberate: a tag is a grouping dimension, not a search term.

- `knowledge.tags` aggregates every tag in the scope (optionally one notebook) with entry counts;
- `knowledge.list` / `knowledge.search` / `knowledge.retrieve` all accept a `tag` parameter for exact filtering;
- the Desktop tag panel filters with one click; click again to clear.

## Import and export

**Import** (`knowledge.import` / `haoyue knowledge import`) adds files as **sources** in the default notebook: titles follow the stable `filename · part N/M` scheme, so **re-importing the same file updates in place** instead of duplicating. With a notebook selected in the Desktop, imported files land in that notebook.

**Export** (`knowledge.export` / `haoyue knowledge export`) renders the whole scope as one Markdown document (scope, timestamp, entry count and each entry's tags) for backup or sharing. The Desktop saves it through the system save dialog; the CLI writes it to a file or prints it to stdout.

## The synonym table

`~/.haoyue/knowledge/synonyms.txt` is your tuning hook for retrieval — **edits hot-reload, no restart needed**:

```text
# comment line
deploy = release, ship      # ":" works as the separator too
key: apikey
```

- one synonym group per line; a query hitting any member expands to the whole group;
- a built-in table of common technical synonyms (deploy/release, build/compile, account/username, error/exception…) is merged underneath: user lines with an existing key extend it, new keys are appended;
- file content is normalized exactly like queries, so full-width characters are fine.

## Using it in Desktop

Open the "Knowledge" page: notebooks live in the left sidebar, entries and sources on the right.

- **notebook sidebar**: an "all entries" aggregate view plus every notebook (with a default badge and entry count); hover a row to rename or delete; `+` creates a notebook (name + description);
- **source panel**: appears above the entry list once a notebook is selected — three toolbar buttons for file import, URL source and text source; expand a source row to preview its text (lazy-loaded first 4000 chars), with refresh and delete actions;
- **entry list**: every entry shows a notebook + source attribution badge, so the "all entries" view always tells you where knowledge came from; search-as-you-type (350ms debounce), automatically scoped to the selected notebook;
- **tag panel**: shows tags with counts for the current scope; click to filter exactly;
- **export**: the toolbar download button opens a save dialog and writes the Markdown backup;
- **synonyms**: the slider button opens the editor with the file path, a reveal-in-folder shortcut, and a discard confirmation when closing with unsaved changes;
- **shortcuts**: `Ctrl+N` new entry, `Ctrl+F` focus search, `Esc` backs out layer by layer (synonyms panel → source form → notebook form → edit form → close page);
- editing an existing entry saves in place by id, so **renaming never forks a duplicate**.

## CLI

```bash
haoyue knowledge list
haoyue knowledge search build
haoyue knowledge add "Title" "Content" --tags a,b
haoyue knowledge show 1
haoyue knowledge delete 1

haoyue knowledge import notes.md minutes.docx
haoyue knowledge export backup.md     # omit the file name to print to stdout
```

`import` reports per file; one failing file does not stop the rest, and the command exits non-zero when any file failed.

## Related IPC methods

| Method | Description |
| --- | --- |
| `knowledge.list` | List entries with notebook/source attribution; `notebookId` and `tag` filters |
| `knowledge.search` | Ranked search inside one notebook, composable with `tag` |
| `knowledge.retrieve` | Cross-notebook search: `notebookIds` / `sourceIds` / `tag` filters, relevance-merged |
| `knowledge.save` | Create or update: same-title upsert, or in-place by `id` (renaming included); `notebookId` sets ownership |
| `knowledge.delete` | Delete by id |
| `knowledge.import` | Bulk import files as sources (auto-chunked, same-file upsert) |
| `knowledge.tags` | Tag aggregation with counts, optionally per notebook |
| `knowledge.export` | Render the scope as Markdown |
| `knowledge.synonyms.get` / `knowledge.synonyms.save` | Read or save the synonym table |
| `knowledge.notebook.list` / `save` / `delete` | Notebook management (the default notebook is permanent) |
| `knowledge.source.add` | Add a source: `kind` is `text` / `url` / `file` |
| `knowledge.source.list` | List the sources of one notebook |
| `knowledge.source.read` | Paged full-text read of one source (`offset` / `maxChars`) |
| `knowledge.source.delete` | Delete a source and its derived entries |
| `knowledge.source.refresh` | Re-read a file / re-fetch a page, rebuilding chunks in place |

::: tip
The agent-side `knowledge_save` / `knowledge_search` / `knowledge_forget` tools share the same store as the IPC above — entries you edit in the Desktop are visible to the agent on its next retrieval, and vice versa.
:::
