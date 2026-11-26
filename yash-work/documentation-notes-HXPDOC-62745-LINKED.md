YG 2026-11-09:

SUMMARY

This phase turns the Hyland Integrations Agent into a reusable, multi-LOB (Line of Business) content assistant that runs as a Chrome (MV3) side panel next to the LOB application the user is already in. From the panel a user can sign in, choose which content system (ECM) to work against, see the documents attached to the on-screen record, search, upload and capture documents, open documents in the CIC Viewer, and do all of this conversationally through a built-in chat. It now supports multiple LOBs - Salesforce, Workday and ServiceNow.

Impact: this is the first release where administrators register content systems and author solution behavior through configuration (not code), and where the agent supports OnBase (via CFS) alongside CIC. Both admin-facing setup docs and end-user task docs need updates. Source development epic: [CONTEN-512](https://hyland.atlassian.net/browse/CONTEN-512)

WHAT CHANGED (delivered in this phase)

- The agent can choose new System Configurations for both CIC and OnBase. ([HXSF-815](https://hyland.atlassian.net/browse/HXSF-815))
- The agent can create/add new Solution Configurations for both Salesforce and Workday, including import mapping and view configuration. (covered via the configuration work in this phase)
- OnBase is supported as an ECM repository alongside CIC. ([HXSF-817](https://hyland.atlassian.net/browse/HXSF-817))
- The Chrome-plugin solution is integrated with the CIC Viewer: when the user asks the agent to open a document, it opens in the CIC Viewer. The viewer renders inside the side panel using an embedded iframe (confirmed - this is the shipped default). ([HXSF-819](https://hyland.atlassian.net/browse/HXSF-819), [HXSF-786](https://hyland.atlassian.net/browse/HXSF-786))
- The agent can search for relevant documents from the list-documents feature. ([HXSF-816](https://hyland.atlassian.net/browse/HXSF-816))
- Workday support in the browser extension: detect the record and list/upload documents. ([HXSF-787](https://hyland.atlassian.net/browse/HXSF-787))
- Natural-language chat UI shell and the "capture" action in the browser plugin. ([HXSF-759](https://hyland.atlassian.net/browse/HXSF-759), [HXSF-760](https://hyland.atlassian.net/browse/HXSF-760))
- MCP server productionization: HTTP/SSE transport, API-key authentication, and public HTTPS dev-tunnel exposure; registered in Agent Builder Studio. ([HXSF-755](https://hyland.atlassian.net/browse/HXSF-755), [HXSF-756](https://hyland.atlassian.net/browse/HXSF-756), [HXSF-757](https://hyland.atlassian.net/browse/HXSF-757), [HXSF-758](https://hyland.atlassian.net/browse/HXSF-758))

NOT IN THIS PHASE / STILL IN PROGRESS (do not document yet)

- Chrome plugin UI/UX redesign to the Figma mockup (Angular/Satori) - in development. ([HXSF-818](https://hyland.atlassian.net/browse/HXSF-818))
- Agent + plugin authentication refinements - in development. ([HXSF-761](https://hyland.atlassian.net/browse/HXSF-761))
- IDP capabilities moving to the "automate" process instead of direct REST APIs - in development; the current REST-API-based IDP is interim. ([HXSF-881](https://hyland.atlassian.net/browse/HXSF-881))
- Deploy the MCP server to the cloud - not started. ([HXSF-762](https://hyland.atlassian.net/browse/HXSF-762))

NEW AND UPDATED FEATURES

1. Chrome side-panel agent (MV3): a context-aware panel that detects the current record (a Salesforce record, a Workday worker, or a ServiceNow ticket) and scopes everything to it; has a Documents view (list + upload) and a Chat view.
2. Sign-in and session: users sign in through Hyland IAM (OAuth 2.0 PKCE); the BFF holds tokens server-side and issues an opaque session, so the browser never holds raw tokens; sessions are reused and refresh silently.
3. System Configurations: after sign-in the user picks a registered content system from the ECM picker - a CIC or an OnBase system (OnBase via CFS); admins or the agent can create one; all later operations use the selected active system.
4. Solution Configurations: each LOB (Salesforce, Workday, ServiceNow) has a configuration defining record-to-metadata mapping, which document types can be uploaded, which fields are shown/editable, which columns appear in the list, and the saved queries; the agent can read and update these, including import mappings and view configuration.
5. Context document list: the panel auto-lists the current record's documents using the default query, with filtering and paging. [Default page size: code default is 10 - confirm.]
6. Search with saved queries: the user picks a configured query, enters criteria using its operators (Equals, Contains, Starts With, and so on), and runs it to get a filtered list.
7. Upload with metadata: the user selects a document type, completes the configured fields, optionally attaches files, and uploads; files are staged out-of-band (bytes never pass through the AI model) and attached to the record.
8. Optional IDP classification: when enabled, the agent auto-detects a file's document type and pre-fills metadata with a confidence indication the user can override. Note: IDP is being reworked to run through the "automate" process instead of REST APIs ([HXSF-881](https://hyland.atlassian.net/browse/HXSF-881), in development) - the current behavior is interim.
9. Workday capture: documents are filed through a single-call capture flow against the worker record, resolving the worker (by name or Employee ID) and the field mappings.
10. Document viewing (CIC Viewer): opening a document shows it in the CIC Viewer, rendered in an embedded iframe inside the panel (shipped default - [HXSF-786](https://hyland.atlassian.net/browse/HXSF-786), [HXSF-819](https://hyland.atlassian.net/browse/HXSF-819)), with a new tab/window as fallback if framing is blocked; previews are fetched as images.
11. Workday document access: opening or downloading a Workday document may return an HxP viewer URL that opens in a first-party tab rather than a direct download.
12. Natural-language chat: requests such as "Show me this customer's contracts" or "Capture this file to the record" make the agent list, search, upload/capture and open documents and reply with clickable document links; results are capped (most recent shown, total count reported).
13. Permissions and Workday authorization: every action runs as the signed-in user; for Workday, a user sees only their own record and their direct reports' records (peers and superiors are refused).
14. MCP server for UCEB: exposes UCEB operations as agent tools (sign-in, list/search/upload/capture/download/delete, open in viewer, read/write System and Solution Configurations, inspect document types/metadata, health/build-info); routes automatically between the LOBs (Salesforce, Workday, ServiceNow) and between content systems; supports local (stdio) and remote (HTTP, API-key protected) hosting.

PREREQUISITES / SETUP

- Supported browser: Chromium-based browser with the Hyland Integrations Agent extension installed (MV3 side panel). [Confirm distribution channel.]
- A reachable UCEB environment and the BFF configured with the Hyland IAM OAuth client (client id/secret, authorize/token/end-session endpoints, and the required scopes).
- At least one registered System Configuration (CIC and/or OnBase) and a Solution Configuration for each LOB in use (Salesforce, Workday, ServiceNow).
- For Workday worker lookup, the BFF must be configured with Workday Staffing API credentials.
- The user must have the appropriate UCEB permissions for the content they access.
- [Supported versions: UCEB [x], Salesforce package [x], Workday tenant requirements [x].]

DOCUMENTATION ACTIONS FOR THE WRITER (what to add / where)

- Installation / Configuration Guide: add a "System Configurations" topic (registering a CIC or OnBase system, selecting the active system via the ECM picker) and a "Solution Configurations" topic (Salesforce, Workday and ServiceNow: import/field mapping, upload document types, display columns, saved queries). Add IAM/OAuth client setup and, for Workday, Workday Staffing API setup. [Link existing [docs.hyland.com](http://docs.hyland.com) topics to extend.]
- Administrator Guide: add how to choose the active content system and (if applicable) enable IDP.
- User Guide: add task topics for sign-in, viewing a record's documents, searching with saved queries, uploading/capturing with metadata, viewing a document in the CIC Viewer, and using chat.
- Troubleshooting: add an entry for "document uploaded but not visible immediately" (search indexing delay) and for Workday "not authorized to view this worker's documents" (self + direct reports only).
- Module Reference Guide (MRG): [list the exact MRG topics that need new/updated entries].

SUPPORTING MATERIAL

- Architecture/flow diagrams (People to AI Agent via Chrome plugin to Hyland Content Services via MCP + UCEB). [Attach the slides / Figma.]
- Demo recording: [link].
- Design/Confluence page: [link].
- Screenshots: ECM picker, documents list, upload card, viewer, chat. [Attach.]

RELEASE NOTES NEEDED

[Yes / No]. The ticket currently indicates N - confirm with the writer whether this phase is customer-releasable or internal-only.

NOTES FOR THE WRITER

- Please treat the chat examples above as representative, not exhaustive.
- Any in-progress "optimistic" document-list behavior (documents appearing immediately after upload, before the search index catches up) is a temporary UX measure and is expected to be reverted - please do not document it as a feature.
- The iframe-vs-new-tab investigation is resolved: the embedded iframe is the shipped default, with a new tab/window as fallback ([HXSF-786](https://hyland.atlassian.net/browse/HXSF-786), [HXSF-819](https://hyland.atlassian.net/browse/HXSF-819)).
- Please hold documentation for the following until they ship: the Figma-based UI/UX redesign ([HXSF-818](https://hyland.atlassian.net/browse/HXSF-818)), authentication refinements ([HXSF-761](https://hyland.atlassian.net/browse/HXSF-761)), the IDP "automate" rework ([HXSF-881](https://hyland.atlassian.net/browse/HXSF-881)), and MCP server cloud deployment ([HXSF-762](https://hyland.atlassian.net/browse/HXSF-762)).
