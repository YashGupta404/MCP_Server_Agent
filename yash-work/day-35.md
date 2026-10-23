# Day 35 — 2026-10-16

_Focus: **post-Satori-migration polish + perf** on the Angular side panel (`agent-ng/`). The new UI
worked but several flows felt slower than the old vanilla-JS popup and a few visual glitches showed up.
Today = find the exact cause of each slowdown (not guess), fix it, and fix two design issues. Plus one
pure infra fix (IDP tunnel). All frontend changes are in `documents.view.*`; backend untouched._

---

## 0. Housekeeping
- Reverted local working tree back to `origin/main` (`git restore --source=origin/main`) to start clean.
- Restarted the demo stack after a VS Code window reload killed the integrated terminals: BFF :5010,
  MCP :5200 (+ warmup login :5005), rebuilt `agent-ng` → `browser-extension/ng`.
- **Lesson:** a window reload kills ALL integrated terminals → BFF, MCP, and the devtunnel all die and
  must be restarted. The symptoms below (connection refused, IDP "unknown type") were caused by this.

## 1. Saved-query dropdown was slow to populate
- **Cause:** old popup fired `loadQueries()` eagerly during context load (fire-and-forget); the new view
  only called it lazily inside `toggleQuery()` (on panel open), so the slow `get_solution_configurations`
  read started only when the user opened the Query panel → "Loading…" for seconds.
- **Fix:** prefetch queries in the background from `reload()` (keyed per business object, with in-flight
  de-dupe so the prefetch + panel-open never double round-trip). `toggleQuery()` just awaits the shared
  promise.

## 2. "documents.query.fieldsLoading" raw key shown
- **Cause:** the template used `documents.query.fieldsLoading` but the key didn't exist in `en.json`.
- **Fix:** added `"fieldsLoading": "Loading search fields…"`.

## 3. Query search FIELDS slow on select
- **Cause:** per-query `get_query_metadata` fired only when a query was picked, with no cache.
- **Fix:** `ensureQueryMeta()` cache + in-flight de-dupe; `onQueryChange()` renders instantly from cache
  and shows no spinner when already warm; stale-selection guard added.

## 4. Upload button showed "Uploading…" during IDP auto-classify
- **Cause:** `autoClassify()` set `this.uploading = true`, and the Upload button text is bound to
  `uploading`.
- **Fix:** removed the `uploading` flag from classify (it means a real upload only); added `!idpBusy` to
  `canUpload` so Upload stays disabled during classify without mislabeling.

## 5. Doc-type metadata fields slow on select
- **Cause:** `loadUploadMetaFields()` called `fetchDocTypeFields` (→ `get_solution_configurations`) every
  time a type was picked, with no cache.
- **Fix:** per-type field cache keyed by type+record (record id included for Workday), in-flight de-dupe,
  background prefetch of the record's CONFIGURED types after doc types load. No spinner when cached.

## 6. "Loading document types…" very slow
- **Cause:** `ngOnInit` loaded doc types LAST and serially — only after `systems.list()` AND the whole
  `reload()` finished. Old popup loaded them in parallel with the document list.
- **Fix:** resolve the record once up front, then run `reload()` + `loadDocTypes()` in parallel
  (`Promise.all`); moved `systems.list()` (header-only) to fire-and-forget so it blocks nothing.

## 7. IDP auto-classify returned "unknown type" for every file
- **NOT a code bug.** The IDP devtunnel (`Idp:PublicBaseUrl = https://qpzmsssb-5010.euw.devtunnels.ms`,
  the port-5010 forward of tunnel `joyful-horse-g88zxrw`) wasn't being hosted after the window reload, so
  IDP's cloud couldn't fetch the staged files → every file timed out to "unknown type". IDP token was
  fresh; the secret URL matched the tunnel exactly (anonymous access). **Fix:** re-host the tunnel. No
  secret change, no BFF restart.
- **Demo reminder:** IDP "unknown type" after a reload ≈ this tunnel is down → re-host `joyful-horse-g88zxrw`.

## 8. White boxes around the auto-filled metadata labels
- **Cause:** the floating field label (`.docs__fieldLabel`) had a hardcoded `background:
  var(--mat-sys-surface)` notch; on the green auto-filled fields (`--mat-sys-tertiary-container`) and the
  grey preview field it showed as a mismatched light box behind the label.
- **Fix:** label-container classes (`docs__field--metaAuto` / `--metaPreview`) make the notch background
  match the field fill (`tertiary-container` / `surface-container`). All tokens → light + dark safe.

## 9. Query SEARCH felt slow
- The search call itself (`runQuery` → `/api/query-execute` → MCP `query_documents` → OnBase CFS) is
  identical to the old popup and backend-bound (OnBase staging latency) — not frontend-reducible.
- **But** a regression from #3: prefetching EVERY query's metadata fired a burst of `get_query_metadata`
  calls on the same MCP→UCEB→OnBase pipeline the search uses, so a search could queue behind it.
  **Fix:** prefetch only the DEFAULT (or first) query's fields; others load on selection (then cached).

## 10. Query results table overflowed out of the panel
- **Cause:** `.docs__table` is `width:100%` but had no horizontal-scroll wrapper, so a result set with
  many display columns exceeded the panel width and spilled out of the extension.
- **Fix:** wrapped the table in `.docs__tableWrap { overflow-x: auto }` (bounded block inside the
  width-constrained card) so wide results scroll horizontally INSIDE the panel.

---

## Net
- All UI slowness the user reported traced to **lazy/serialized loading** vs the old popup's
  parallel/eager loading, plus one **over-aggressive prefetch** that fixed the dropdowns but starved the
  search. Fixes = prefetch + cache + de-dupe + parallelize, tuned so background work doesn't contend with
  user actions.
- Two design fixes: label-notch color match, and a horizontal-scroll wrapper for wide query tables.
- One infra fix: re-hosted the IDP devtunnel (the real cause of "unknown type").
- `ng build` green after every change; output → `browser-extension/ng`. Backend (BFF/MCP) unchanged.
