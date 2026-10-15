# Day 34 — 2026-10-14

_Focus: **Satori Design System adoption** for the Angular side panel (`agent-ng/`). Manager asked to
migrate the existing UI/UX into Hyland's official **Satori** framework. Today = research + foundation +
the full Angular upgrade needed to even install Satori._

---

## 1. What Satori is (research)
Satori is Hyland's next-gen design system — the global standard across the Content Innovation Cloud.
It's **not just CSS/theme**; it reshapes the Angular low-level design:
- **Baseline**: Angular 18+ with Angular Material on Material Design 3 (MD3).
- **Packages** (private, GitHub Packages): `@hylandsoftware/satori-ui` (components), `satori-icons`
  (SVG, no font icons), `satori-devkit` (app shell: header, app switcher, profile, session/auth via
  `oidc-client-ts`), `satori-viewer` (document viewer), `satori-lint`.
- **Theming**: MD3 CSS system variables (`var(--mat-sys-*)`), light + dark, no hardcoded hex, no `::ng-deep`.
- **i18n**: standardized on `ngx-translate`.
- **House rules**: filled cards only, sentence-case buttons (primary last), skeleton "ghost" loaders (no
  spinners), inline field errors, Satori alert banners, WCAG 2.2 AA, Hyland Plainspeak ("Select" not "Click").

## 2. Foundation added
- **`.github/copilot-instructions.md`** — Satori engineering guardrails tailored to this repo (our views,
  overlays, kebab menu, delete modal, viewer overlay, upload selects). Grounds Copilot for every future
  UI prompt.
- **`agent-ng/.npmrc.template`** + `.gitignore` ignores the real `.npmrc` (so the PAT never gets committed).

## 3. Decisions (with Yash)
- **Theme**: support BOTH light + dark, with a **visible user toggle switch** in the panel.
- **Shell**: adopt the **full `@hyland/satori-devkit` app shell** (replaces the custom `.docs__header`).

## 4. Registry access (PAT)
- Satori packages are private on GitHub Packages → need a GitHub **Personal Access Token** with
  `read:packages`. Confirmed required: an unauthenticated `npm view @hylandsoftware/satori-ui` → **401**.
- Yash is a member of the Hyland orgs; created a **classic** token (fine-grained tokens are flaky with
  `npm.pkg.github.com`), put it in `agent-ng/.npmrc`. Auth then succeeded.
- **Correct scope is `@hylandsoftware`** (NOT `@hyland` — those 404'd).

## 5. The big one: Angular 18 → 22 upgrade
Satori's current `latest` needs Angular 22, so the app had to be upgraded.

### Version detective work
| Satori UI | Needs Angular |
|---|---|
| 0.1.3 | 19 |
| 0.2.0 | 20 |
| 0.3.0 | 21 |
| **0.4.2 (latest)** | **22** |

- `satori-devkit@0.4.2` is the **strictest** peer: `@angular/* ~22.0.8` (only 22.0.x, not 22.2). So the
  whole framework was pinned to **exact 22.0.8** (not `^`, which would let npm pull 22.2 and re-break devkit).
- Angular 22 requires **TypeScript 6.0** (`>=6.0 <6.1`) → pinned `~6.0.3`.
- Satori peers **`ngx-translate ^17`** (not the latest 18).

### Final pins (`package.json`)
- Framework: `@angular/* 22.0.8` exact (common/core/platform-browser/-dynamic/router/forms/animations/
  compiler/compiler-cli); `@angular/cdk 22.0.7`, `@angular/material 22.0.7` (their 22.0.x top patch).
- Tooling: `@angular/cli` + `@angular-devkit/build-angular 22.0.9`; `typescript ~6.0.3`; `zone.js ~0.16.0`.
- Satori: `satori-ui ^0.4.2`, `satori-icons ^3.11.0`, `satori-devkit ^0.4.2`, `satori-viewer ^0.5.3`,
  `satori-lint ^0.4.2` (dev).
- i18n: `@ngx-translate/core` + `http-loader ^17`.
- DevKit's own peers added as deps: `oidc-client-ts ^3.3.0`, `bestzip ^2.2.1`.

### Install
- First attempt `^22.2.2` → **ERESOLVE** (devkit wanted `~22.0.8`). Re-pinned to exact 22.0.8 → clean install.
- Installed OK: `@angular/core 22.0.8`, `satori-ui 0.4.2`. Satori packages present: satori-ui, -devkit,
  -viewer, -icons, -lint, plus transitive **-tokens, -fonts, -illustrations**.

## 6. Fixing the one breaking change
- **NG6008**: Angular 19+ makes components **standalone by default**. Components declared in an NgModule
  now need explicit `standalone: false`. The 3 views already had it; **`AppComponent` didn't** → added
  `standalone: false` to `app.component.ts`.
- NG8107 (`context?.x` optional-chain hints) are harmless warnings, left as-is.

## 7. Result
- `ng build` → **EXITCODE=0**, bundle written to `browser-extension/ng`. The existing UI is visually
  unchanged but now runs on the **Satori-compatible Angular 22 stack**. Ready for actual Satori skinning.

### Gotcha: terminal corruption
The persistent PowerShell terminal repeatedly corrupted (core cmdlets / PATH "not recognized"). Workaround
used all day: invoke via full path `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` +
`C:\Program Files\nodejs\npx.cmd`, redirect output to a `*.log` file, and read it back (PowerShell
`Out-File` writes UTF-16, so strip ANSI + re-encode UTF-8 when needed).

## 8. Next (not started)
1. Wire Satori theme/tokens into `styles.scss` + `ngx-translate` + `BrowserAnimationsModule`; add the
   **light/dark toggle**.
2. Integrate the **devkit app shell** (replace custom header).
3. Refactor views in order **sign-in → ecm-picker → documents** (slices): MD3 tokens for all hardcoded
   hex (~300 across 4 files), inline SVGs → `satori-icons`, custom cards/table/pager/overlays → Satori
   components, strings → `ngx-translate` keys.

---

## 9. Phase 1 — theme + i18n foundation + dark/light toggle (DONE, builds green)

Studied the installed Satori packages directly (docs aren't shipped — only READMEs + `.d.ts`/SCSS):
- `@hylandsoftware/satori-ui/theme` exposes a Sass **`theme()`** mixin that wraps Angular Material 3's
  `mat.theme` and emits the MD3 **system variables** (`--mat-sys-*`). Because the tokens use the CSS
  `light-dark()` function, **light/dark is driven by `color-scheme`** — no second theme build needed.
- `@hylandsoftware/satori-ui/providers` exposes **`provideSatori()`** (theme + icons + dynamic form-field
  subscript sizing) — returns `EnvironmentProviders`, valid in an NgModule `providers` array.
- i18n: ngx-translate v17 uses **`TranslateModule.forRoot()`** (NgModule) or `provideTranslateService()`.
- NOTE: `@hylandsoftware/satori-devkit` is a heavyweight SaaS app shell (OIDC auth via `oidc-client-ts`,
  platform nav, Pendo analytics). It conflicts with this panel's **BFF-session** auth and its ~400px
  width → **deferred + flagged**; revisit whether the full shell is appropriate vs. just Satori header
  components.

### Changes
- **`src/styles.scss`**: `@use '@hylandsoftware/satori-ui/theme'` + `html { @include satori.theme(); color-scheme: light; }`
  and `html.dark-theme { color-scheme: dark; }`. Body now uses `var(--mat-sys-surface)` / `on-surface` /
  `body-medium` instead of hardcoded hex + Inter stack (Satori ships its own fonts).
- **`src/app/app.module.ts`**: added `BrowserAnimationsModule`, `TranslateModule.forRoot()`, and
  `provideSatori()`.
- **`src/app/services/theme.service.ts`** (new): `ThemeService` toggles the `dark-theme` class on the root,
  seeds from `localStorage` then OS `prefers-color-scheme`, and persists the choice.
- **`app.component.ts`**: calls `theme.init()` at startup.
- **documents header**: added a **sun/moon theme toggle button** (`theme.toggle()`), styled with
  `--mat-sys-*` tokens; injected `ThemeService` into `DocumentsView`.
- **`angular.json`**: raised the initial bundle budget (2 MB warn / 3 MB error) — the full MD3 theme adds
  ~700 kB of CSS, which is fine since the bundle loads locally from the extension (no network cost).

### Result
- `ng build` → **EXITCODE=0**, bundle → `browser-extension/ng`. Theme tokens are live app-wide and the
  dark/light toggle works. Existing component SCSS still has hex (gets migrated per-view in Phase 2).

## 10. Next (Phase 2)
- Refactor **sign-in** first (smallest) as the token/`satori-icons`/`ngx-translate` pattern, then
  **ecm-picker**, then **documents** in slices. Decide the devkit-shell question with Yash before the header.

---

## 11. Devkit decision + Phase 2 start

### Compliance call: Option A (chosen)
Asked which is Hyland-compliant: (A) Satori components + keep BFF-session auth, or (B) full
`satori-devkit` app shell. **Answer: A.** Satori "compliance" = tokens/components/icons/a11y/i18n — both
A and B satisfy that. The **devkit is an opt-in shell for standalone platform apps** (it runs its own OIDC
login via `oidc-client-ts`, platform nav, app switcher, Pendo). This panel is an **embedded Salesforce/
Workday side panel using BFF-session auth** in a ~400px frame, so forcing the devkit would mean a second,
conflicting auth model and an oversized platform header — i.e. misapplying it. **A is both compliant and
architecturally correct.** (If the mandate were "must plug into the platform app-switcher," that's an
architecture change to OIDC, not a design-compliance one.) → Proceeding with **A**.

### i18n infrastructure wired
- `app.module.ts`: `TranslateModule.forRoot({ fallbackLang:'en', lang:'en', loader: {provide: TranslateLoader,
  useClass: TranslateHttpLoader} })` + `provideHttpClient()` + `TRANSLATE_HTTP_LOADER_CONFIG` ({ prefix:'i18n/',
  suffix:'.json' }). Translation files load from `public/i18n/*.json` → served at `ng/i18n/en.json`.
- New **`public/i18n/en.json`** holds the translation keys (starting with `signIn.*`; grows per view).

### Sign-in view refactored (first Satori view, pattern template)
- Key fact: `satori-ui` only ships a FEW custom components (app-header, avatar, breadcrumbs, logo,
  platform-nav, rich-tooltip, tag). **Buttons / cards / inputs come from Angular Material MD3**, which
  Satori themes. Material v22 button API = `<button matButton="filled|outlined|tonal|text|elevated">`.
- `sign-in.module.ts`: imports `MatButtonModule`, `MatCardModule`, `TranslateModule`.
- `sign-in.view.html`: custom `<div class="signin__card">` → **`<mat-card appearance="filled">`**; custom
  primary `<button>` + CSS spinner → **`<button matButton="filled">`** (busy = disabled + text swap, no raw
  spinner); all strings → **`translate` pipe** keys; error is an inline **alert** (`role="alert"`, token-styled).
- `sign-in.view.ts`: inject `TranslateService`; default error uses `translate.instant('signIn.error')`.
- `sign-in.view.scss`: stripped ALL hex/rgba (was ~30 colors + obsolete email/SSO/remember/or/ms-icon/
  spinner blocks) → now pure `--mat-sys-*` tokens (surface-container, headline-small, body-medium,
  error-container, corner-*). File shrank ~220 → ~65 lines.
- `.gitignore`: added `build.log` / `build.clean.txt` / `install.log` (terminal-capture temp files).

### Result
- `ng build` → **EXITCODE=0**, bundle generation complete. Sign-in now renders as a Satori/MD3 filled card
  with a themed filled button, localized text, dark/light aware. It's the reference pattern for the rest.

## 12. Next
- **ecm-picker** (cards + selection state) → then **documents** in slices (header/search/query, table +
  paginator, upload form + fields, overlays: kebab menu / delete dialog / viewer — all `panelClass:'satori'`,
  chat). Keep extracting strings into `en.json`.

---

## 13. ecm-picker refactored (Satori icons + tags + tokens)

Learned the icon + tag APIs from the installed packages:
- **Satori icons** render via Angular Material **`<mat-icon svgIcon="name">`** — `provideSatori()` already
  registers them globally (`provideSatoriIcons`). Valid names come from
  `@hylandsoftware/satori-icons/icon-list.json` (e.g. `panel_close`, `check`, `cloud`, `more_vert`,
  `refresh`, `download`, `trash`, `search`, `filter`, `send`, `upload`, `light_mode`/`dark_mode`,
  `onbase`/`salesforce`/`nuxeo`). There is **no plain `close`** — use `panel_close`.
- **Badges/tags** → `<sat-category-tag category="purple|blue|pink|teal|yellow|green|red|orange|gray">`
  from `@hylandsoftware/satori-ui/tag` (a standalone component imported into the view's NgModule).

### Changes
- `ecm-picker.module.ts`: imports `MatButtonModule`, `MatIconModule`, `TranslateModule`, `SatCategoryTag`.
- `ecm-picker.view.ts`: `badgeClass()` → **`badgeCategory()`** returning a `SatTagCategory`
  (onbase→blue, cic→green, nuxeo→teal, else gray); errors now via `translate.instant(...)`.
- `ecm-picker.view.html`: close `×` glyph → `matIconButton` + `<mat-icon svgIcon="panel_close">`; title/
  subtitle/default/errors → `translate` keys; systemType badge → **`<sat-category-tag>`**; the per-row CSS
  **spinner → "Connecting…" text** (house rule: no raw spinners); check glyph → `<mat-icon svgIcon="check">`;
  error is an inline alert (`role="alert"`).
- `ecm-picker.view.scss`: all hex/rgba → `--mat-sys-*` tokens; filled list items
  (`surface-container-high`, no border — filled not outlined); removed the badge-color classes,
  spinner + shimmer keyframes; skeleton ghost now a token-based opacity **pulse**.
- `en.json`: added the `ecm.*` keys.

### Result
- `ng build` → **EXITCODE=0**, generation complete. ecm-picker is now Satori filled-card tiles with
  Satori category tags, Satori icons, localized copy, and full light/dark theming.

## 14. Next
- **documents** view — the big one. Do it in slices: header (toggle already there; convert remaining
  icons + avatar), search + query panel, table + `mat-paginator`, upload form + dynamic metadata fields,
  overlays (kebab `mat-menu`, delete `mat-dialog`, viewer) all with `panelClass:'satori'`, then the chat tab.
  Keep extracting strings into `en.json`.

---

## 15. documents view — SCSS fully tokenized + header icons (builds green)

The documents view is the largest (~1200 TS, ~400 HTML, ~1150 SCSS). Pushed through the whole thing in
verified slices, building between each.

### Module
- `documents.module.ts`: added `MatButton/Card/Dialog/FormField/Icon/Input/Menu/Paginator/ProgressBar/
  Select/SnackBar/Tooltip` modules, `TranslateModule`, and `SatCategoryTag`.

### Header slice (done)
- All inline header SVGs → `<mat-icon svgIcon>`: theme toggle → `light_mode`/`dark_mode`, avatar check →
  `check`, close → `panel_close`, system cloud → `cloud`, refresh → `refresh`, collapse → `chevron_up/down`,
  record doc → `document`. Buttons are now `matIconButton`. Tooltips/aria via `translate` keys
  (`app.*`, `documents.*`).

### SCSS — ALL hex → tokens (the dominant compliance win)
Converted the **entire ~1150-line `documents.view.scss`** from ~200 hardcoded hex/rgba values to
`--mat-sys-*` tokens, in batches: header/system-card/tabs, search + query panel, table/kebab/menu/pager,
chat (bubbles/typing/markdown/composer), viewer, upload card (dropzone/file list/IDP badge/fields/selects/
metadata/buttons), delete modal, and bottom toast. Notable mappings: surfaces →
`surface`/`surface-container(-low/high/highest)`, text → `on-surface`/`on-surface-variant`, brand →
`primary`/`on-primary`/`primary-container`, errors → `error`/`error-container`, chat agent bubble →
`surface-container-high`, user bubble → `primary`, code block → `inverse-surface`, toast →
`inverse-surface`/`inverse-on-surface`, IDP badges → `tertiary`/`error`/`secondary` containers, file-type
icons → `error`/`primary`/`tertiary`/`secondary`/`outline`. Verified **zero** plain hex remain (only a
couple of decorative data-URI SVG backgrounds keep an embedded stroke color — slated for `mat-icon` swap).
- `ng build` → **EXITCODE=0**, generation complete; `styles.css` ~29.7 kB. Light/dark both drive off the
  tokens now, so the whole documents screen themes correctly.

### Still open on documents (template/behavior, next)
- Remaining inline SVGs / CSS-icons → `satori-icons`: chat send (paper-plane) + attach (+), dropzone icon,
  menu download/delete icons, upload arrow glyph, search/filter pseudo-icons, file-type letter badges →
  `pdf`/`excel`/`image`/`document` icons.
- `<select>`/`<input>` → `mat-form-field` + `matInput`/`matSelect` (doc type, metadata, query, search).
- Overlays → Material with `panelClass:'satori'`: kebab floating menu → `mat-menu`; delete modal →
  `mat-dialog`; bottom toast → `MatSnackBar`.
- Buttons → `matButton`: upload, run query, auto-classify, chips, viewer actions.
- Extract the remaining documents/upload/chat strings into `en.json`.

---

## 16. documents template — icons, i18n, kebab → mat-menu (builds green each slice)

Pushed through the documents template in verified slices:
- **Search / query / table / pager**: search + filter pseudo-icons → `<mat-icon svgIcon="search|filter">`;
  file-type letter badges → `<mat-icon [svgIcon]="fileIconName(d)">` (new `fileIconName()` maps kind →
  `pdf`/`document`/`excel`/`image`/`note`); kebab `⋮` → `matIconButton` + `more_vert`; pager `‹ ›` →
  `matIconButton` + `chevron_left/right`; strings → `documents.*` keys.
- **Chat**: empty title/sub, chips, placeholder, attach/send/remove → `chat.*`; attach `+` and send
  paper-plane SVGs → `<mat-icon svgIcon="attach|send">` (kept bespoke circular buttons); file-chip `×` →
  `panel_close`.
- **Upload card**: dropzone icon + upload-button `↑` → `upload`, remove `×` → `panel_close`; all strings
  (title `{{type}}` param, dropzone, doc-type, metadata states, linked record, note, auto-classify, CTA,
  IDP badges) → `upload.*` keys.
- **Kebab menu → `<mat-menu class="satori">`** (overlay guardrail): replaced the custom floating menu div
  with a Material menu triggered by `[matMenuTriggerFor]`; `mat-menu-item` + `<mat-icon svgIcon=
  "download|trash">` + `documents.menu.*`; removed the unused `docs__menuIcon` data-URI CSS.
- **Viewer bar**: back/open-tab/loading/fallback strings → `viewer.*`; back arrow → `arrow_left` mat-icon.
- Verified **zero literal hex** in `documents.view.scss` (only 2 URL-encoded data-URI colors remain: the
  `<select>` chevron and chat-avatar sparkle).

**Result:** `ng build` → **EXITCODE=0** after every slice; `documents-module` ~294 kB (MatMenu etc.).

### Remaining on documents (smaller, riskier TS)
- Delete modal → `MatDialog` (`panelClass:'satori'`) and bottom toast → `MatSnackBar` — both need TS
  restructuring of the `deleteTarget`/`actionToast` flows; the custom versions are already fully
  token-styled + functional.
- `<select>`/`<input>` → `mat-form-field` (doc type/metadata/query/search) — polish.
- Remove now-dead `toggleMenu`/`menuTop`/`menuLeft` TS; swap the 2 data-URI icons; chat empty illustration
  → `@hylandsoftware/satori-illustrations` (optional).

---

## 17. Overlays → Material: delete modal → MatDialog, toast → MatSnackBar (builds green)

Finished the last overlay guardrail items (the riskiest — they touch the TS flows):
- **Delete modal → `MatDialog`** (template-based, no extra component): added `<ng-template #deleteDialog>`
  with `mat-dialog-title` / `mat-dialog-content` / `mat-dialog-actions` and `matButton` / `matButton=
  "filled"` actions. `remove()` now does `dialog.open(this.deleteDialog, { panelClass: 'satori',
  autoFocus: false })`; `cancelDelete()` closes the ref; `confirmDelete()` deletes, closes on success,
  and shows the error inline in the dialog on failure. Strings → `delete.*` keys.
- **Toast → `MatSnackBar`**: replaced the custom `actionToast` state + bottom toast div with
  `snackBar.open(text, action, { panelClass: 'satori', duration })`. Loading = persistent (no action);
  success/error = 5 s auto-dismiss with a "Dismiss" action. Download/delete messages → `actions.*` keys
  with `{{name}}` params.
- Injected `MatDialog`, `MatSnackBar`, `TranslateService` into `DocumentsView`; added `delete.*` +
  `actions.*` to `en.json`.
- `ng build` → **EXITCODE=0**; `documents-module` ~298 kB.

### Migration status
- **sign-in, ecm-picker, documents** — all three views now Satori: MD3 tokens (zero hardcoded hex),
  `satori-icons`, `sat-category-tag`, Material components themed by Satori, all overlays via
  `mat-menu`/`mat-dialog`/`MatSnackBar` with `panelClass:'satori'`, full `ngx-translate` i18n, and a
  working light/dark toggle. Builds green throughout.

### Optional polish left (non-blocking)
- `<select>`/`<input>` → `mat-form-field` + `matInput`/`matSelect` (doc type/metadata/query/search).
- Remove now-dead `toggleMenu`/`menuTop`/`menuLeft`/`menuFor` TS + unused modal/toast SCSS.
- Swap the 2 remaining data-URI icons (`<select>` chevron, chat-avatar sparkle) to `mat-icon`.
- Chat empty-state illustration → `@hylandsoftware/satori-illustrations`.




