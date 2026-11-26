# Day 54 — 2026-11-25

## Mentor feedback on the core UI diagram -> as-is + to-be diagrams

Mentor: the diagram should show the component name on each UI control and the service dependencies, so we know
the as-is state and can define how the app should be structured: smart/dumb components, reusable UI controls /
directives (dropdowns, typeaheads), theming, a11y, translations, a facade layer consumed via models, a provider
layer per third-party tool (KE, IDP, other agents), centrally evaluated feature flags (UI and/or service layer).
After the LLD/design patterns are final -> identify stories -> start working on them.

### Done
- Mapped every core UI control to its owning component and the exact service methods it calls (grep of
  `this.docSvc/chat/systems/ctxSvc/auth/bff/theme` in the views + service method list).
- New diagrams (PNG 2x + SVG + `.mmd` source) in `yash-work/diagrams/`:
  - `as-is-components-services` — 3 views, each control with its Angular/Satori control type, edges labelled
    with service methods, services -> BffApiService -> BFF / Chrome tab.
  - `to-be-architecture` — 8 layers: shell+routing (guards), smart pages, dumb components, shared UI kit
    (hyl-dropdown, hyl-typeahead, hyl-skeleton, *hylFeature, directives), cross-cutting (theme, i18n, a11y,
    FeatureFlagService, error handling, models), facades (interfaces + InjectionTokens), providers (UCEB/BFF,
    IDP, KE, Agent Builder, other agent, Chrome tab, Workday, IAM), infrastructure (HttpClient + interceptors).
    FeatureFlagService feeds route guard, template directive and provider selection.
- `angular-component-diagram.md` section 8: as-is control table, to-be layer rules, page -> children -> facades
  -> providers mapping, 5 decisions needed before stories.

### Next
- Review the to-be LLD with the mentor, settle the 5 decisions, then break it into stories.
