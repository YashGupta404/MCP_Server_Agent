# Day 32 — 2026-10-12

_Focus: **UX revamp kickoff**. Manager asked for the **Low-Level Design (LLD)** of the Hyland Integrations
Agent Angular frontend, built to **exactly mirror** the reference repo
[`HylandFoundation/OIFS-hifs-client`](https://github.com/HylandFoundation/OIFS-hifs-client). Studied that
repo thoroughly (architecture, structure, state, routing, auth, build) and designed the LLD on top of it.
UI mockups will be shared next — this LLD is the structural foundation they slot into._

**Afternoon:** scaffolded the Angular app and implemented the **first mockup — the Sign-in screen**.

---

## PART C — Angular scaffold + Sign-in screen (first Figma mockup)

### C1. Scaffolded the workspace (`agent-ng/`)
- `npx @angular/cli@18 new agent-ng --no-standalone --routing --style=scss --skip-tests` → **NgModule-based**
  (not standalone) to mirror OIFS. Node v24, npm 11, Angular 18. `npm install` OK (943 pkgs). `npm run build`
  **succeeds** — the `sign-in-module` builds as a **lazy chunk** (confirms lazy routing works).
- Raised `anyComponentStyle` budget in `angular.json` (2kB→6kB) so the styled card doesn't warn.

### C2. OIFS-style structure created
```
agent-ng/src/app/
  types/app-config.model.ts        AppConfig/AuthConfig/EntraTenant interfaces
  services/
    settings.ts                    static Settings.config (OIFS pattern)
    app-config.service.ts          loads /configs.json before bootstrap
    auth/agent-auth.service.ts     login() -> launches IAM (PKCE), never takes the password
  views/sign-in/
    sign-in.view.ts/.html/.scss    the screen
    sign-in.module.ts              NgModule + RouterModule.forChild
  app-routing.module.ts            hash routing, lazy '' -> sign-in
  app.component.html/.scss         shell (<router-outlet>)
public/configs.json                runtime config (BFF url, entra tenants, auth endpoints)
src/main.ts                        config-first bootstrap (OIFS-style)
```

### C3. Sign-in screen — the **recommended** design (decided with Yash)
- **Decision:** we do NOT collect the password in our own UI. OAuth/OIDC + Entra SSO require the password to
  be entered on the **Nucleus IAM-hosted page**. So the screen is a **branded launch pad**, and its buttons
  *start* the real IAM flow.
- Built exactly that: logo tile, "Sign in to Hyland" / "Connect your content repositories", **email row +
  Change** (email → IAM `login_hint`), **Remember me**, **Continue**, an **OR** divider, and the two
  **"Sign in with Entra ID (tenant)"** buttons (tenant → IAM `domain_hint`). **Password field + Forgot
  password dropped** (can't be used against IAM). MS 4-square logo drawn in CSS.
- `AgentAuthService.login({loginHint|domainHint, remember})` generates **PKCE** (code_verifier/challenge),
  stores verifier+state, builds the authorize URL, and redirects. In the extension this `window.location`
  redirect becomes `chrome.identity.launchWebAuthFlow`. `acr_values=idp:local` keeps the Hyland page for the
  email path; `domain_hint` federates straight to the tenant for the Entra buttons.
- Entra tenants + default email come from `configs.json` (data-driven, no hard-coding). `clientId`/redirect
  are placeholders in `configs.json` to fill from the real client.

### C4. How to run it
`cd agent-ng` → `npm start` (→ `ng serve`) → open `http://localhost:4200`. (node_modules/dist are gitignored.)

### C5. Next mockups
Await the next Figma screen (likely the post-login **context panel / document list**) → build as the
`context-panel` view with its Akita `state/` per the LLD.

---

## PART D — Angular build IS the extension panel (no localhost)

Decision with Yash: **one UI = the revamped Angular one, running inside the actual plugin** (not a localhost
app). Clarified the misconception: `ng serve`/localhost is only a dev preview; `ng build` emits static files
that the extension loads from `chrome-extension://…` — no server at runtime.

Wired it up (step 1 — the sign-in screen now opens in the real extension side panel):
- **angular.json build** → `outputPath: ../browser-extension/ng` (flattened, `browser:""`), `baseHref: "./"`,
  `outputHashing: none`, and **`optimization.styles.inlineCritical: false`** (MV3 CSP forbids the inline
  `onload="this.media='all'"` handler Angular/Critters injects — disabling critical-CSS inlining removes it).
- **manifest.json** → `side_panel.default_path` changed `src/popup.html` → **`ng/index.html`** (the Angular build).
- **.gitignore** → ignore `agent-ng/dist/` and `browser-extension/ng/` (build output).
- `npm run build` → emits `browser-extension/ng/{index.html, main.js, chunk-*.js, styles.css, configs.json}`.
  Verified index.html has **no inline scripts/handlers** → loads under MV3 CSP.
- Old vanilla UI preserved at `src/popup.html` (revert = one line).

**To see it:** `chrome://extensions` → reload the extension → open the side panel on the Salesforce tab →
the revamped **Angular sign-in** renders inside the plugin (no localhost).

**Migration plan (one UI, functional):** per Figma screen, build the Angular view AND port its real
BFF logic from `popup.js`/`agent.js` into an Angular service. Order:
sign-in ✅ → **ECM picker (Choose content system)** → Documents list (search, Query, row actions
Preview/Download/Delete, pagination) → Upload (+IDP) → Talk-with-agent (chat) → saved-query panel.
Context comes from the existing `detector.js` content script via `chrome.runtime` messaging →
Angular `ContextDetectorService`; data via the same BFF `:5010`. When all ported, retire `popup.*`.

### Dev loop (no localhost)
`cd agent-ng; npm run build` (or `ng build --watch`) → reload extension. `npm start`/:4200 is optional
visual-only preview (can't use `chrome.*`).

## PART A — OIFS-hifs-client architecture study (the reference)

**What it is:** OnBase Integration for Salesforce (HIFS) client — an Angular app embedded in the Salesforce
UI (canvas/VisualForce), talking to OnBase (documents, workflow, custom queries) over REST + GraphQL.

### A1. Workspace & bootstrap
- **Angular multi-project workspace**: code lives under `projects/hifs-client/` (+ `projects/hifs-client-e2e/`).
- **NgModule-based, NOT standalone** — every component is `standalone: false`; features are `@NgModule`s.
- **Runtime-config bootstrap** (`src/main.ts`): `new AppConfigService().appConfig$()` fetches
  `assets/configs.json` into a **static `Settings.config`** BEFORE `platformBrowserDynamic().bootstrapModule(AppModule)`.
  Uses `hammerjs` (touch), `enableProdMode()`. Config-before-bootstrap = endpoints/auth aren't compiled in.
- **`AppComponent`** = thin shell: `<app-hifs-toolbar>` + `<router-outlet>` + mobile footer; registers custom
  SVG icons from `assets/svg_icons/entries.json`.

### A2. Folder layout (`src/app/`)
```
core/        core.module.ts  shared.module.ts  material.module.ts   (singletons + re-exports)
services/    app-config.service, settings (static), platform-detection, app-events,
             scrollbar-width, route.guard, hifs-http-interceptor,
             onbase(.interface|-demo), salesforce(.interface|-demo),
             preferences/, logging/, auth/(hifs-auth-service, popup-auth-vf, route-state,
                                           hifs-auth-initializer, hifs-auth-factory, *-guard)
types/       app-config.model, SFDC_configuration, sfdc-parameters, languages, app-wide-settings
utils/       hifs-auth-util
directives/  hide.directive
environments/ environment(.prod), hifs-constants
assets/      configs.json, svg_icons/entries.json, i18n
views/       <feature modules — see A3>
```

### A3. Feature modules (`views/`) — the main organizing unit
Each view is a **self-contained NgModule** with its own `RouterModule.forChild(ROUTES)`, a `components/`
folder, optional `services/`, and (if stateful) a `state/` folder.
- Naming convention: **top-level screen = `*.view.ts`** (e.g. `home.view.ts`, `dashboard.view.ts`,
  `admin.view.ts`); **child pieces = `*.component.ts`**. Templates `.view.html`/`.component.html`.
- Views present: `home/`, `dashboard/`, `ob-grid-view/` (doclist), `document/` (viewer),
  `ob-capture/` (upload), `admin/`, `migration/`, `hifs-toolbar/`, `common/` (snackbar/dialog/spinner),
  `confirming-authorization/`, `post-authorization/`, `post-logout/`, `unknown-route/`,
  `ob-keyword-controls/`, `utils/grid-utility/`.

### A4. State management = **Akita** (`@datorama/akita`)
Per-feature `state/` folder, one concept per file:
- `*.model.ts` — entity interface (e.g. `DocumentModel { id, name, documentDate, qid }`).
- `store/*.store.ts` — `EntityStore<State>` + `@StoreConfig({ name })`; optional `EntityUIStore` for UI flags
  (e.g. `isSelected`).
- `query/*.query.ts` — `QueryEntity` selectors (observables the components subscribe to).
- `service/*.service.ts` — business logic: calls an HTTP service-util, writes to the store.
- Dev: `AkitaNgDevtools` + `AkitaNgRouterStoreModule`.
- Simpler cross-cutting state uses plain **`BehaviorSubject`** (e.g. `AppEventsService.routedPage$`,
  `RouteStateService`, `AdminStateService`, `ObDocumentUploadStateService.currentFiles$`).

### A5. Routing (`app.routing.ts`)
- **Lazy-loaded** feature modules: `loadChildren: () => import('./views/x/x.module').then(m => m.XModule)`.
- **Hash routing** `RouterModule.forRoot(routes, { useHash: true, initialNavigation: 'enabledNonBlocking' })`
  — needed because the app is hosted embedded (iframe/canvas).
- **Guards**: `canActivateAuthGuard` (`@hyland/ui`), `AuthGuardVFService`, `HifsRouteActivationGuard`,
  `SalesforceContextLoaderGuard` (loads host context before the view).
- **Two route sets** (`routesForRedirectAuth` vs `routesForPopupAuth`) swapped at runtime by
  `RouteConfigurationService` based on the `popup_login` config flag. Each route carries `data:{routePath}`.

### A6. Auth & HTTP
- `angular-oauth2-oidc` `OAuthService` + `@hyland/ui` `HyAuthModule`/`HyAuthService` + custom
  **`HifsAuthService`** (`HifsAuthState`, storage keys/prefixes), redirect + popup flows.
- `APP_INITIALIZER` → `hifsAuthInitializerFactory` boots auth; `OAuthStorage` from `HifsAuthUtil.getAuthStore()`.
- **`HifsHttpInterceptor`** (`HTTP_INTERCEPTORS`, multi): attaches bearer token, catches 401 → re-auth,
  errors → snackbar.
- OnBase document/keyword data via **Apollo GraphQL** (`apollo-angular`, `APOLLO_OPTIONS` factory);
  workflow/customquery/task via REST behind **InjectionTokens** (`OB_DOCUMENT_API`, `OB_WORKFLOW_API`,
  `OB_CUSTOMQUERY_API`, `OB_TASK_API`) whose `useFactory` reads `Settings.config`.

### A7. UI toolkit, i18n, responsiveness, demo, build
- `@hyland/ui` (HyAuth, HyTranslate, HyLog, HyViewer, HyDualList, HyPullToRefresh), `@hyland/ui-shell`
  (`HyShellModule.forRoot()` + `HY_SHELL_CONFIG_INITIALIZER`), `@hyland/onbase` (ObViewer, ObApiDocument,
  ObKwpIndexKeywordPanel, ObAuthentication/ObSession), Angular **Material** (`MaterialModule` re-exports),
  **Infragistics IgniteUI** grids (`@infragistics/igniteui-angular` + `@hyland/ui-infragistics`).
- **i18n**: `HyTranslateModule` (Transloco) — en/es/de/fr/pl; Infragistics locale strings too.
- **Responsive**: `PlatformDetectionService.isMobile$()` + desktop/mobile component variants.
- **Demo mode**: parallel `*-demo` services (`onbase-demo`, `salesforce-demo`) swapped via DI `useClass`
  when `demo_mode` — also used for tests.
- **Testing**: **Jest** (`jest-preset-angular`) primary, per-feature `test/` modules + `Mock*` services;
  Protractor e2e. **Build/package**: Angular CLI `dist` → Cake/WiX MSI → Hyland Platform ServiceHost.

---

## PART B — LLD: Hyland Integrations Agent (Angular), mirroring OIFS

Same conventions as OIFS: Angular workspace, **NgModule feature-views**, **Akita** state, `core`/`shared`/
`material` modules, `services`/`types`/`utils`/`directives`, lazy **hash** routing with guards, `@hyland/ui`
shell, an HTTP interceptor, runtime `AppConfigService` + static `Settings`, and `*-demo` services.

> Key difference vs OIFS: the Integrations Agent talks to **our BFF** (`/api/*`), not OnBase GraphQL. So the
> "data-util" layer is a single **`BffApiService`** (HttpClient) instead of Apollo. Everything else mirrors OIFS.

### B1. Workspace & bootstrap
- Workspace: `projects/integrations-agent/` (+ `-e2e`). New folder in the repo, e.g. `agent-ng/`.
- `main.ts`: `AppConfigService.appConfig$()` loads `assets/configs.json` (BFF base URL, IDP flags, auth
  config) into static `Settings.config`, then bootstraps `AppModule`.
- `AppComponent` shell: `<agent-toolbar>` + `<router-outlet>` (+ optional chat dock).

### B2. Folder layout (`src/app/`)
```
core/        core.module.ts  shared.module.ts  material.module.ts
services/    app-config.service, settings (static), platform-detection, app-events,
             agent-http-interceptor, route.guard, context-detector.service,
             bff/bff-api.service,
             auth/(agent-auth-service, auth-initializer, route-state, auth.guard),
             demo/(bff-api-demo, …)
types/       app-config.model, record-context.model, uceb-document.model,
             document-type.model, metadata-field.model, idp-result.model,
             worker-match.model, system-config.model, chat-turn.model
utils/       token-util, file-util
directives/  hide.directive, drag-drop.directive
environments/ environment(.prod), agent-constants
assets/      configs.json, svg_icons/, i18n/
views/       <feature modules — see B3>
```

### B3. Feature views (map existing extension → OIFS-style modules)
| View module | OIFS analogue | Responsibility (from today's extension) |
|---|---|---|
| `shell-toolbar/` | `hifs-toolbar/` | top bar: sign in/out, **ECM picker**, record header, connection status |
| `home/` | `home/` | landing + auth entry point |
| `context-panel/` | `dashboard/` + `ob-grid-view/` | the record's **document list** (table + columns), filter/search, worker-resolve + "not authorised" states |
| `capture/` | `ob-capture/` | **upload card**: dropzone, doc-type, metadata form, **IDP auto-classify/extract** panel + review badges |
| `document-viewer/` | `document/` | the viewer overlay (`ObViewerModule`) |
| `chat/` | _(new, same pattern)_ | **chatbot** turns to the cloud agent |
| `system-config/` | part of `admin/` | ECM/system-config picker dialog |
| `common/` | `common/` | snackbar, dialog, spinner, badges |
| `confirming-authorization/`, `post-authorization/`, `post-logout/`, `unknown-route/` | same | OAuth lifecycle pages |

Naming mirrors OIFS: screen = `*.view.ts`, children = `*.component.ts`.

### B4. Services (cross-cutting, `services/`)
- `app-config.service.ts` + `settings.ts` + `types/app-config.model.ts` — runtime config: `BFF_BASE_URL`,
  `IDP_ENABLED`, `AUTH_CONFIG`, `demo_mode`, `storage`.
- `bff/bff-api.service.ts` — thin `HttpClient` wrapper; adds `X-BFF-Session`; central error mapping. (This
  replaces OIFS's Apollo/ObApiDocumentService + service-utils.)
- `auth/agent-auth.service.ts` — OAuth PKCE / BFF session (port of current `auth.js`), `auth-initializer`
  (`APP_INITIALIZER`), `route-state.service`, `auth.guard`.
- `context-detector.service.ts` — bridges the page's record context (the current `content/detector.js`
  stays a vanilla content script; it `postMessage`s context in, this service exposes it as an observable).
- `platform-detection.service.ts`, `app-events.service.ts` (`AppRoutedPageEnum`), `agent-http-interceptor.ts`
  (attach session, 401→re-auth, error→snackbar).
- `demo/` — `BffApiDemoService` etc., swapped by `demo_mode` (OIFS pattern) for mockups/offline demos.

### B5. State (Akita, per feature `state/`) — mirrors OIFS exactly
- `context-panel/state/`: `record-context.model.ts`, `uceb-document.model.ts`; `store/document.store.ts`
  (`EntityStore` + `EntityUIStore` for `isSelected`), `query/document.query.ts`,
  `service/document.service.ts` (calls `BffApiService` `/api/context`, writes store).
- `capture/state/`: `upload-file.model.ts` (≈ OIFS `FileCaptureStateModel`), `idp-result.model.ts`;
  `store/upload.store.ts`, `query/upload.query.ts`, `service/upload.service.ts`, `service/idp.service.ts`
  (`/api/idp/classify|extract|status`).
- `chat/state/`: `chat-turn.model.ts` + store/query/service (`/api/chat`).
- `system-config/state/`: `system-config.model.ts` + store/query/service (`/api/systems`).
- Simple cross-cutting state (selected record, auth-in-progress) via `BehaviorSubject`, like OIFS.

### B6. Models (`types/`) — the data contract (port of today's JSON shapes)
`RecordContext` (businessObjectType, businessObjectId, source, displayName, notAuthorized),
`UcebDocument` (docId, name, attributes, columns), `DocumentType`, `MetadataField` (id, label, importable),
`IdpClassResult` (docType, confidence, reviewRequired), `IdpExtractedField`, `WorkerMatch` (wid, name),
`SystemConfig`, `ChatTurn`, `AppConfig`.

### B7. Routing (`app.routing.ts`)
- Lazy `loadChildren` per view; **hash routing** (`useHash: true`) because the app is hosted embedded
  (extension side-panel/iframe) — same reason as OIFS.
- Guards: `authGuard` (session present), `contextLoaderGuard` (resolve record context before `context-panel`).
- Routes carry `data:{routePath}`; `unknown-route` fallback.

### B8. Host integration (MV3 / where it runs)
- The Angular app replaces the **popup/side-panel UI** only. `background.js` (service worker) and
  `content/detector.js` (page scraper) **stay** as vanilla extension scripts; the detector `postMessage`s the
  record context to the Angular app → `ContextDetectorService`.
- MV3 CSP: ship the **AOT production build** (no eval/inline), set `angular.json` `outputHashing: none` + fixed
  output dir so `manifest.json` can point `action.default_popup`/side-panel at Angular's `index.html`.
- (If later hosted as a standalone web app/iframe instead of an extension, the hash-routing + runtime-config
  design already supports it — same as OIFS in the SF canvas.)

### B9. Build / tooling (mirror OIFS)
- Angular CLI workspace, **Jest** (`jest-preset-angular`) + per-feature `test/` mock modules, `@hyland/ui`
  + Material + (optional) Infragistics grid for the doc table, `HyTranslate` i18n, `*-demo` services.

### B10. Data-flow sequences (to include as diagrams in the LLD doc)
1. **Sign-in**: Toolbar → `AgentAuthService.login()` → OAuth/BFF session → `authGuard` opens `context-panel`.
2. **List documents**: `contextLoaderGuard` → `DocumentService.load(ctx)` → `BffApiService /api/context` →
   `DocumentStore` → `DocumentQuery` → `DocumentListComponent` renders.
3. **Auto-classify**: `IdpClassifyPanel` → `IdpService.classify(files, ctx)` → `/api/idp/classify` (batched
   job) → `UploadStore` fills detected type + extracted fields → review badges → user clicks Upload.

---

## Next
- **Waiting on the new UI mockups** from manager → map each screen to the view modules in B3, refine the
  component tree + Material/Hy components per screen, then scaffold `agent-ng/` with these module/service/state
  skeletons.
- Deliverable to manager: this LLD (component tree + service/state spec + sequence diagrams) then the scaffold.

## Reference repo map (quick index for implementation)
`projects/hifs-client/src/main.ts` (bootstrap) · `app/app.module.ts` (root wiring) · `app/app.routing.ts`
(lazy+hash+guards) · `app/core/{core,shared,material}.module.ts` · `app/services/*` (auth, interceptor,
config, onbase/salesforce ± demo) · `app/views/<feature>/{*.view.ts, components/, services/, state/}` ·
Akita `state/{model,store,query,service}`.

---

## PART E — Debugging "documents load but don't render" in the Angular panel (Salesforce account)

**Symptom (user):** On a Salesforce Account (`001gK00001KbuonQAB`, active system `OnBase9714_Integrations_Agent`)
the revamped Angular panel's Documents tab shows the column headers (Document Name / Document Date / Document
Type) but **no rows**, even though the record has documents.

**Confirmed the whole server path WORKS (ruled out BFF/MCP):**
- MCP reachable, active system = `OnBase9714_Integrations_Agent`.
- `list_documents(account, 001gK00001KbuonQAB)` → **51 docIds**.
- `query_documents(account, queryId=115, filterFieldId=223, filterValue=001gK00001KbuonQAB, maxResults=500)`
  → **51 docs with full columns**: `Document Name="Invoices - 10/8/2026"`, `Document Date=2026-10-08T…`,
  `Document Type=Invoices/COM - Application`. So the data + the BFF default-list path return rich rows.
- BFF `GetDefaultListQueryAsync("account")` resolves dlq id 115 + boContextField 223 + columns
  [Document Name, Document Date, Document Type]; `ParseDocumentList` emits `{docId,name,type,attributes}` =
  exactly what the Angular `UcebDocument` model expects. Angular `cell()`/`displayColumns` map all three cols.

**Ruled out:** stale build (built `browser-extension/ng/main.js` @2:43 is newer than source), missing module
imports (`CommonModule`+`FormsModule` present), SCSS clipping (table rows styled normally), session/BFF conn
(ECM picker uses the same session and works). Code + data are both correct — couldn't repro statically.
CDP `open_browser_page` only reaches the OLD file-based `popup.html`, not the live chrome-extension side panel,
so runtime inspection isn't available that way.

**Action:** added a TEMP on-screen diagnostic line to `documents.view.html` (orange `DBG loading=… error=…
ctx=… docs=… cols=… paged=… sys=…`) + rebuilt. Need the user to **close & reopen the side panel** (reloads
`ng/index.html` → fresh build) and read the DBG line to get the true runtime state (docs count / context /
error). Leading hypothesis: the panel instance on screen was opened before the latest working build (side
panels don't auto-reload on `ng build --watch`). **TODO: remove the DBG line once root-caused.**

**Root cause #1 — stale cached build.** The DBG line confirmed `docs=51 cols=3 paged=7 loading=false error=""`
(data loads perfectly), so it was purely a render issue. But reopening the panel kept showing the OLD render —
`chunk-YHW4SY36.js` on disk had my diagnostics yet the panel didn't show them. Changing the marker to pink
`BUILD-v3` and doing a proper reload (right-click panel → Inspect → Ctrl+Shift+R, or close panel → reload ext
on `chrome://extensions` → reopen) finally loaded it. So every earlier "reopen" was serving a stale cached
Angular bundle. Lesson: after `npm run build`, the side panel needs a real reload (not just toggle) to pick up
the new `ng/` bundle; a distinctive marker confirms freshness.

**Root cause #2 — flaky render (`*ngFor` over getter-arrays).** With the fresh build the user saw the rows
flash then vanish while DBG still showed `paged=7`. The documents list bound `*ngFor` to **getters**
(`filtered`/`paged`/`displayColumns`) that each returned a **brand-new array on every change-detection pass**.
Under the Salesforce Lightning page's constant CD churn (the `tabs.onUpdated` storm feeding `onChange`), that
getter-array anti-pattern makes Angular re-diff/tear-down the rows and the list flickers out. **Fix
(`documents.view.ts`):** converted `filtered`/`paged`/`displayColumns`/`totalPages` to **stored fields**
recomputed by a single `recompute()` only when data/search/page change; added `trackByDocId`/`trackByCol` to
the `*ngFor`s; and hardened `reload()` so a transient null context (mid-navigation) **never wipes an
already-loaded list**. Removed all diagnostics. Clean build `chunk-QYKRCB37.js` @3:06. Pending: user reloads
the panel to confirm the doc rows render and stay.

**Root cause #2 (THE ACTUAL ONE) — CSS flex-shrink clipped the card (user spotted it: "going out of card
size / loading message gone down and hidden").** `.docs__scroll` is a flex COLUMN; `.docs__mainCard` has
`overflow: hidden` (for the rounded corners). A flex item with `overflow:hidden` resolves `min-height:auto`
to **0**, so flex-shrink is free to **squash the main card below its content height**, and because it's
`overflow:hidden` the table rows / "Loading documents" message get **CLIPPED instead of scrolled** (the
upload card has no overflow:hidden so it keeps its size and squeezes the main card). That's why headers
showed but rows didn't, and why "Loading documents" was cut off at the card's bottom edge. **Fix
(`documents.view.scss`):** `.docs__card { flex: none; }` so cards keep their natural height and `.docs__scroll`
scrolls instead of clipping. Build `chunk-3LAZ45IB.js` @3:11. (The getter→field + trackBy refactor above is
still a valid robustness improvement, but THIS flex clip was the real "docs don't show" bug.)

## PART F — Ported the in-panel document VIEWER to the Angular UI (row click → CIC viewer)
Replicated the old popup's viewer behavior exactly: clicking a document **row** opens it in an in-panel
iframe overlay.
- `document.service.ts`: `openInViewer(docId)` → BFF `POST /api/viewer` → MCP `open_document_in_viewer` →
  returns the full CIC viewer URL. Verified live: docId 392 →
  `https://cic-viewer.staging.app.hyland.com/#/cfs/4873af36-dd9a-4e90-9b54-962dc4079fb1/392/1?envKey=appintel-staging-test`
  (CFS form `/#/cfs/{integrationId}/{contentId}/1`, same as before).
- `documents.view.ts`: viewer state + `preview(doc)` (row click) → `openInViewer` → `openViewer(url)` which
  binds the iframe `src` via `DomSanitizer.bypassSecurityTrustResourceUrl`, with a 30s load watchdog →
  `showViewerFallback()` (the SPA never fires `load` when framing is blocked), `onViewerLoaded()` to clear the
  watchdog, `closeViewer()`, Escape-to-close, and `openInViewerWindow()`/`openInViewerTab()` fallbacks
  (`chrome.windows.create` popup / `chrome.tabs.create`).
- `documents.view.html`: `<tr class="docs__row" (click)="preview(d)">` + the `.docs__viewer` overlay
  (bar with Back + title + Open-in-tab, body with iframe/loading/fallback).
- `documents.view.scss`: `.docs__row{cursor:pointer}` + `.docs__viewer*` overlay styles.
- Framing/cookies UNCHANGED and still apply: manifest `host_permissions` include `cic-viewer.staging.app.
  hyland.com`, no restrictive `content_security_policy` (MV3 default allows the https iframe), and the DNR
  header-strip (`rules.json`) + `background.js` SameSite=None cookie promotion are extension-level, so the
  Angular iframe uses the identical path the old popup did. Build: `documents-module` chunk `chunk-7PXO53ZI.js`
  (41.6 kB), clean. Pending: user reloads + clicks a row to confirm the viewer renders.

## PART G — Ported the server-side QUERY (keyword search) feature to the Angular UI
Replicated the old popup's Query flow exactly (toggle panel → pick query → keyword inputs with operators +
validation → search → results replace the list → Clear restores the default list).
- `document.service.ts`: `fetchQueries(boType)` (BFF `/api/queries` → solution-config `queryConfig[boType]`),
  `fetchQueryMetadata(queryId)` (BFF `/api/query-metadata` → `get_query_metadata`, `{inputs, resultColumns}`),
  `executeQuery({...})` (BFF `/api/query-execute` → `query_documents`). Added `QueryInfo/QueryInput/QueryMeta`.
- `documents.view.ts`: `toggleQuery()`, `loadQueries()`, `onQueryChange()` (loads+caches metadata, seeds the
  per-input operator), `operatorsFor()`, `friendlyOperator()` (same token→label map as popup), `collectQueryFilter()`
  (first filled input wins; required + minLength validation), `runQuery()` (sets `documents`+`columns` to the
  query results' `resultColumns`, `recompute()`, `queryActive=true`), `clearQuery()` (reload default list).
- `documents.view.html`: Query button now `(click)="toggleQuery()"` with an active state + the `.docs__queryPanel`
  (query `<select>`, per-input operator `<select>` from `supportedOperators` + value input with `maxlength`,
  Search/Clear buttons, status line).
- `documents.view.scss`: `.docs__queryPanel`/`.docs__queryField`/`.docs__queryOp`/`.docs__queryVal`/
  `.docs__queryRun` etc.
- Verified live against the active OnBase9714_Integrations_Agent: `list_queries` → 9 account queries
  (Invoices 109, Commercial Lending Search 110, Docs by sObjectId 115, CL Loan Number 111, Patient queries…);
  `get_query_metadata(109)` → input "Vendor Name" (228, ops Equals/StartsWith/Contains, maxLength 250) +
  resultColumns (Vendor Name/Invoice #/Invoice Total/Invoice Date/Document Type/Document Name/…). Those columns
  flow through `cell()` (Document Name→name, Document Type→type, rest→attributes). Build `main.js` @3:35, clean.
  Pending: user reloads + tries a query.

**Redesign to match Figma (3:44):** first cut overflowed (operator select pushed the value input off the panel).
Rebuilt the panel to the Figma mock: header "Search the repository with a saved query" + **Reset** link, outlined
**floating-label** fields (`.docs__qField`/`.docs__qLabel`), **Match** (108px) + value input side by side in
`.docs__qRow` with `min-width:0` everywhere so it fits the panel width, and a full-width purple **Run query**
button. `runQuery()` already replaces the list with ONLY the matching docs; Reset restores the default list.

## PART H — Ported "Talk with agent" (chatbot) to the Angular UI
Full chat feature through the existing cloud path: **BFF `/api/chat` → Agent Builder → dev tunnel → MCP**.
- `utils/markdown.ts`: ported the old popup's safe Markdown renderer (escape-first; headings/bold/italic/
  inline+fenced code/http links/lists/tables). Rendered via `DomSanitizer.bypassSecurityTrustHtml`.
- `services/chat.service.ts`: `ChatService.send(message, attachments, context)` → `POST /api/chat`
  (`{message, conversationId, attachments, businessObjectType, businessObjectId}`) → `reply`. One
  `conversationId` (crypto.randomUUID) per app session for multi-turn continuity.
- `documents.view.ts`: chat state (`chatTurns`, `chatInput`, `chatSending`, `chatFiles`), `sendChat()`
  (adds user turn → "Agent is thinking…" → agent reply as sanitized markdown → `openViewerFromReply()` →
  refresh doc list if files were uploaded), `quickPrompt(text)` (sets input + sends — wires the fast-prompt
  chips), `onChatFilePick`/`removeChatFile`, `buildOutgoing()` (appends the on-screen record hint so "this
  record" resolves), `openViewerFromReply()` (opens a viewer link from the reply in the in-panel viewer),
  `fileToAttachment()` (base64), `scrollChat()` (ViewChild `#chatLog`).
- `documents.view.html`: replaced the chat placeholder with a real log (empty state → user/agent/error/typing
  bubbles), the two **quick-prompt chips** ("I want to search a document", "Show my documents") now
  `(click)="quickPrompt(...)"`, an attach list, and a composer (+ attach, text input with Enter-to-send,
  send button).
- `documents.view.scss`: chat log scroll, user (purple, right) / agent (grey, left) / error / typing bubbles,
  markdown styling (lists/tables/code/links), chips, attach list, composer + send button.
- Build `main.js` @4:10, chat chunk `chunk-HCB3ARPR.js`, clean. Requires the MCP dev tunnel + Agent Builder
  running (as before). Pending: user reloads + chats / clicks a fast-prompt chip.

**Design pass to match Figma (4:16):** agent/error messages now show a **sparkle avatar** (inline SVG on a
light-purple circle) to the left of the bubble; messages wrapped in `.docs__msgRow` (user right, agent left).
Replaced the "Agent is thinking…" text with an **animated three-dot typing bubble** (`.docs__typing` — dots
rise sequentially via `@keyframes docsTyping` with staggered `animation-delay`). Send button made circular.
Raised `angular.json` anyComponentStyle budget 16→32kB (the view's SCSS grew past 16kB). Clean build.
