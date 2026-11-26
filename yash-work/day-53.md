# Day 53 — 2026-11-24

## Mentor task: AI-generated Angular component diagram -> review gaps -> production work items

Mentor: "create a component diagram (Angular components, services etc. and its composition — which component is
parent and what are its children) using AI from your Angular code. Review it, find gaps, and create work items
needed for production."

### Done
- Analysed `agent-ng/src` (modules, routes, components, templates, constructor injections, BFF calls, line counts,
  guardrail scan). Output: `yash-work/angular-component-diagram.md` with:
  1. Bootstrap + routing diagram (main.ts -> AppModule -> AppComponent/router-outlet -> 3 lazy views).
  2. Components -> services -> BFF diagram + endpoint table per caller.
  3. Inside `DocumentsView` (one component holding 8 features).
  4. 16 gaps (G1–G16) with evidence.
  5. Proposed target composition (child components, guards, interceptors, shared store).
  6. 16 draft work items (W1–W16) with acceptance criteria + priority, and 3 open questions.

### Key facts found
- 3 views (SignIn, EcmPicker, Documents), each the ONLY component in its lazy module: no child components.
- `DocumentsView`: 1655 ts / 436 html / 1658 scss, ~95 methods, 13 constructor deps.
- 0 spec files; no route guards; BFF client uses `fetch` (not HttpClient) -> manual zone/CD handling.
- Guardrail scan: 21 `<mat-icon>`, 0 satori-icons imports, 12 hex colors, 20 inline styles, 8 spinners,
  46 `: any`, 3 `console.*`, ~9 hardcoded English text nodes in the PoC cards.

### Next
- Review the diagram + gaps with the user/mentor; agree the work items and answer the 3 open questions.

### Core UI diagram without the PoCs (user request)
- User: the AI context detector and the solution mapper (Auto-map) were only for testing and will be removed
  later (not now); wants the component diagram of the core UI, laid out like the actual screens.
- Added section 7 to `angular-component-diagram.md`: screen flow; Sign-in, System picker (core part) and Documents
  drawn as nested regions top to bottom (header, system card, tabs, query panel, table, pager, chat, upload card,
  overlays) with the child component each region should become; parent -> children table.
- Core-only findings: duplicated header (picker + documents); hardcoded English in core screens (app name/tag,
  tab labels, row tooltip, IDP badge tooltips, "Agent is thinking"); hex colors = 10 in the chat SVG + 1 in
  sign-in scss; spinners in 6 places; native `<select>`s; hand-built table + pager instead of the Satori table +
  `mat-paginator`; leftover PoC code in DocumentsView (`detectAiContext`). New items W17 (selects), W18 (Satori
  table + paginator), W19 (shared header).
