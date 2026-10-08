# Knowledge Base

The knowledge base is Haoyue's long-term fact store: the agent saves and retrieves facts during conversations through the `knowledge_save` / `knowledge_search` / `knowledge_forget` tools, and you can maintain it by hand in the Desktop or the CLI. Entries are isolated per scope — the current workspace or global — persisted in SQLite, and survive restarts and upgrades.

## How retrieval works

Knowledge search deliberately uses **substring matching** instead of full-text indexing: SQLite's tokenizers do not segment CJK text, and substring matching is what makes mixed Chinese/English queries actually hit. The server applies a full preprocessing pipeline to every query:

- **Normalization**: Unicode FormKC folding + lowercasing, so full-width input equals half-width;
- **CJK bigrams**: Chinese queries are sliced into two-character pieces, letting unspaced questions hit;
- **Synonym expansion**: query terms are expanded through the synonym table (see below);
- **Typos**: edit distance ≤ 1 for short words, ≤ 2 for long ones (applied to titles and tags);
- **Scoring**: title +3, content +2, tags +2; ranked by coverage first, then score, then recency.

## Tags: exact grouping

Tags are comma-separated (full-width `，` `；` accepted too) and match by **whole-tag equality** (case-insensitive) — the tag `build` does not match `buildtool`. That is deliberate: a tag is a grouping dimension, not a search term.

- `knowledge.tags` aggregates every tag in the scope with entry counts;
- `knowledge.list` and `knowledge.search` both accept a `tag` parameter for exact filtering;
- the Desktop tag panel filters with one click; click again to clear.

## Import and export

**Import** (`knowledge.import` / `haoyue knowledge import`) splits files into knowledge entries:

- supports txt / md / csv / code files (UTF-8 with GBK fallback), docx and xlsx;
- 10 MB per file; content is chunked at 6000 characters, up to 200 entries per file;
- titles follow the stable `filename · part N/M` scheme, so **re-importing the same file updates in place** instead of duplicating.

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

Open the "Knowledge" page:

- **tag panel**: shows tags with counts for the current scope; click to filter exactly;
- **export**: the toolbar download button opens a save dialog and writes the Markdown backup;
- **synonyms**: the slider button opens the editor with the file path, a reveal-in-folder shortcut, and a discard confirmation when closing with unsaved changes;
- **shortcuts**: `Ctrl+N` new entry, `Ctrl+F` focus search, `Esc` backs out layer by layer (synonyms panel → edit form → close page);
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
| `knowledge.list` | List entries, optional exact `tag` filter |
| `knowledge.search` | Ranked search, composable with `tag` |
| `knowledge.save` | Create or update: same-title upsert, or in-place by `id` (renaming included) |
| `knowledge.delete` | Delete by id |
| `knowledge.import` | Bulk import from files (auto-chunked, same-file upsert) |
| `knowledge.tags` | Tag aggregation with counts |
| `knowledge.export` | Render the scope as Markdown |
| `knowledge.synonyms.get` / `knowledge.synonyms.save` | Read or save the synonym table |

::: tip
The agent-side `knowledge_save` / `knowledge_search` / `knowledge_forget` tools share the same store as the IPC above — entries you edit in the Desktop are visible to the agent on its next retrieval, and vice versa.
:::
