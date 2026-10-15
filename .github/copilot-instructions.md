# Satori Design System — Engineering Guardrails

You are assisting in migrating the existing Angular UI of the **Hyland Integrations Agent**
side panel (`agent-ng/`) into Hyland's **Satori Design System**. Follow these rules for every
UI change in this workspace.

## Scope
- The frontend lives in `agent-ng/` (Angular 18, NgModule-based, hash routing).
- It builds into `browser-extension/ng/` and runs as a Chrome MV3 side panel.
- Views: `sign-in/`, `ecm-picker/`, `documents/` (documents list + upload + chat).
- Screen components are named `*.view.ts`; child pieces are `*.component.ts`.

## 1. Technology stack & core rules
1. **Framework**: Angular 18+ with Angular Material adopting Material Design 3 (MD3).
2. **Satori packages** (private, `@hylandsoftware` / `@hyland` scopes on GitHub Packages):
   - Components: `@hylandsoftware/satori-ui`
   - Icons: `@hylandsoftware/satori-icons` (never Material font icons or raw inline SVGs)
   - App shell / DevKit: `@hyland/satori-devkit`
   - Document viewer: `@hyland/satori-viewer` (for the in-panel viewer overlay)
   - Lint: `@hylandsoftware/satori-lint` (enable `no-material-font-icons`)
3. **Localization**: `ngx-translate`. No hardcoded user-facing strings, no Transloco,
   no `@angular/localize`.

## 2. Theming & tokens
- **Never hardcode hex colors, rgba, borders, or static typography.** Use MD3 CSS system
  variables:
  - Surfaces: `var(--mat-sys-surface)`, `var(--mat-sys-surface-container)`, `var(--mat-sys-on-surface)`
  - Brand/accent: `var(--mat-sys-primary)`, `var(--mat-sys-on-primary)`, `var(--mat-sys-outline)`
  - Typography: `var(--mat-sys-body-medium)`, `var(--mat-sys-title-small)`, `var(--mat-sys-label-large)`
- All styling must support **Light and Dark themes** without contrast regressions.
- **No `::ng-deep`.** Use Angular Material override mixins (`@include mat.*-overrides(...)`)
  or Satori tokens.

## 3. CDK overlay & floating panels
Overlay content does not inherit container styles — attach the Satori class explicitly:
- Dialogs: `MatDialog.open(Cmp, { panelClass: 'satori' })`
- Bottom sheets: `MatBottomSheet.open(Cmp, { panelClass: 'satori' })`
- Datepickers: `panelClass="satori"` on `<mat-datepicker>` / `<mat-date-range-picker>`
- Autocompletes: `class="satori"` on `<mat-autocomplete>`
- Paginators: `[selectConfig]="{ panelClass: 'satori' }"` on `<mat-paginator>`

This matters here for: the row-actions kebab menu, the delete confirmation modal, the
in-panel viewer overlay, and the upload doc-type/metadata selects.

## 4. Component, layout & house rules
- **Cards**: filled cards only (`satori-card` / filled variant). Never outlined cards.
- **Buttons**: sentence case ("Upload document", "Run query", "Save changes"); the primary
  action is placed **last** (rightmost).
- **Forms & inputs**: group related fields under headers in Satori section cards; put field
  requirements as helper/hint text under the input (not tooltips); keep existing
  `[(ngModel)]` / Reactive Forms bindings intact while wrapping them in Satori form fields.
- **Data tables**: use the Satori table wrapper with standard sort headers + paginator for
  the documents list; row actions go through Satori action bars / menu triggers.
- **Loading states**: never raw spinners. Use Satori skeleton "ghost" cards that match the
  expected layout (documents table rows, upload card).
- **Feedback**:
  - Field validation errors render inline beneath the control (`<mat-error>`), never snackbars.
  - Snackbars/toasts only for transient confirmations (auto-dismiss ~4s) or dismissible
    errors with an action (e.g. "Retry").
  - Persistent/page-level warnings use inline Satori alert banners.
- **Icons**: zero font icons. Use `@hylandsoftware/satori-icons` SVG components with
  `aria-label` (interactive) or `aria-hidden="true"` (decorative).

## 5. Low-level design & state
- Model explicit view states in components: `loading` (ghost), `loaded`, `empty`, `error`.
  The documents view already tracks `loading` / `error` / empty — formalize these four.
- Keep presentational Satori wrappers separate from the BFF service/business logic
  (`DocumentService`, `ChatService`, `BffApiService` stay unchanged).
- Extract all strings into `ngx-translate` keys (e.g. `documents.upload.cta`).

## 6. Microcopy & accessibility (WCAG 2.2 AA)
- Hyland Plainspeak: use "Select", never "Click"/"Tap"; verb-first imperative copy at or
  below a grade-9 reading level.
- Logical tab order, no keyboard traps, focus return after dialog dismissal.
- Associate labels/errors programmatically (`aria-describedby`, `aria-invalid`,
  `aria-expanded`). Support RTL (`dir="rtl"`).

## 7. How to respond when refactoring a file
When given a file to migrate, structure the answer in this order:
1. **Architectural & LLD impact** — state/overlay/form bindings affected.
2. **Template & markup diff** — HTML moved to Satori components + icons.
3. **Styling diff** — SCSS moved to MD3 system variables (no hex, no `::ng-deep`).
4. **i18n keys** — new `ngx-translate` entries required.

Flag any custom control with no direct Satori equivalent and propose the approved Satori
composite pattern before writing a workaround.
