# Documentation Notes — HXPDOC-62745 (Hyland Integrations Agent – Next phase)

> Copy the block below (from `YG 2026-11-09:` onward) into the **Documentation Notes** field
> on HXPDOC-62745. Do **not** delete Soumya's existing entry — add yours underneath hers.
> Replace anything in **[square brackets]** with a real value before you save.

---

YG 2026-11-09:

**Summary**

This phase of the Hyland Integrations Agent moves it from a single-purpose plugin to a
reusable, multi-LOB content assistant that runs as a Chrome (MV3) side panel next to the
business application the user is already working in. From that panel a user can sign in,
choose which content system (ECM) to work against, see the documents attached to the
record on screen, search, upload and capture new documents, open documents in the viewer,
and do all of the above conversationally through a built-in chat. The same agent now works
across **Salesforce** and **Workday** records against both **CIC** and **OnBase** content
backends, driven by per-application Solution Configurations and per-environment System
Configurations rather than one-off code. This affects both administrators (who register
systems and author configurations) and end users (who browse, search, upload and view
content in-context).

Under the covers the panel talks to a Backend-for-Frontend (BFF) that holds the user's
session, and a new **Model Context Protocol (MCP) server** that exposes Hyland's UCEB
(Unified Content and Experience Backend) as a set of agent-callable tools. The MCP server
is the component that lets the AI agent perform real content actions — list, search,
upload, capture, view and delete documents — as the signed-in user, with that user's
permissions always honored.

**Related work**

| Issue | Relationship |
|---|---|
| CONTEN-512 | Development epic this documentation issue was generated from (Done). |
| [CONTEN-xxx] | [child issue — e.g. System Configuration support] |
| [CONTEN-xxx] | [child issue — e.g. Solution Configuration support] |
| [CONTEN-xxx] | [child issue — e.g. CIC Viewer integration] |
| [CONTEN-xxx] | [child issue — e.g. OnBase / CFS support] |

**New and updated features**

1. **Chrome side-panel agent (MV3).** A context-aware side panel that detects the record
   the user is viewing (Salesforce record or Workday worker) and scopes everything it shows
   to that record. The panel has a Documents view (list + upload) and a Chat view.

2. **Sign-in and session.** Users sign in once through Hyland IAM using an interactive
   OAuth 2.0 PKCE flow. The BFF owns the client secret and the user's tokens server-side and
   issues the panel an opaque session; the browser never holds raw tokens. The session is
   reused when the panel is reopened. Tokens refresh silently on expiry.

3. **System Configurations (choose your content backend).** After sign-in the user picks a
   registered content system from the ECM picker — for example a **CIC** system or an
   **OnBase** system (OnBase is reached through CFS federation). Administrators can create a
   System Configuration and the agent can also create and register one. All subsequent
   operations (list, search, upload, view) run against the selected active system.

4. **Solution Configurations (per-application behavior).** Each line of business
   (Salesforce, Workday) is driven by a Solution Configuration that defines: how the record
   on screen maps to content metadata, which document types a user may upload for a given
   record type, which metadata fields are shown and editable, which columns appear in the
   document list, and the saved search queries available. The agent can read and update these
   configurations, including import mappings and the document-list (view) configuration.

5. **Context document list.** The panel automatically lists the documents attached to the
   current record, using the configured default query for consistent columns and ordering.
   Users can filter the list and page through results. [Confirm default page size — code
   defaults to 10.]

6. **Search with saved queries.** Users open the query panel, pick a configured query (for
   example "Invoices by Customer" or "Contracts by Date"), enter criteria using the query's
   operators (Equals, Contains, Starts With, and so on) and run it to get a filtered list.

7. **Upload with metadata.** Users select a document type, complete the metadata fields
   configured for that document type and record, optionally attach one or more files, and
   upload. Files are staged to the server out-of-band (the file bytes never pass through the
   AI model) and attached to the record in the active content system.

8. **Optional IDP (Intelligent Document Processing) classification.** When enabled, the
   agent can auto-detect an uploaded file's document type and pre-fill metadata fields, with
   a confidence indication that the user can review and override before uploading.
   [Confirm whether IDP ships in this phase or is behind a flag.]

9. **Workday capture.** For Workday, documents are filed through a single-call capture flow
   against the worker record, including resolving the correct worker (by name or Employee ID)
   and the business-object field mappings.

10. **Document viewing (CIC Viewer).** When a user opens a document, it is shown in the
    Hyland viewer. For CIC (and OnBase via CFS) the document renders in an **embedded iframe**
    inside the panel; the user can also choose to open it in a **new browser tab/window** if
    framing is blocked. Thumbnails/previews are fetched as images rather than framed content.
    [Confirm the final default: embedded iframe vs. new tab.]

11. **Workday document access.** For Workday, opening or downloading a document may return a
    Hyland Experience (HxP) viewer URL that opens in a first-party tab rather than a direct
    file download.

12. **Natural-language chat.** Users can type requests such as "Show me this customer's
    contracts", "Search for invoices from last month", "Capture this file to the record", or
    "Open the first one". The agent calls the MCP tools to list, search, upload/capture and
    open documents, and replies with clickable document links. Listed results are capped
    (most recent shown, with the total count reported).

13. **Permissions and Workday authorization.** Every action runs as the signed-in user and
    honors that user's permissions. For Workday, the agent and panel enforce that a user may
    only see their own record and their direct reports' records; access to peers or superiors
    is refused.

14. **MCP server for UCEB.** A new MCP server exposes UCEB operations as agent tools
    (sign-in, list/search/upload/capture/download/delete documents, open in viewer, read and
    write System and Solution Configurations, inspect document types and metadata, and health
    or build-info checks). It routes automatically between Salesforce (`/api`) and Workday
    (`/bow`) based on the signed-in user, and between content systems based on the selected
    active system. It supports both local (stdio) and remote (HTTP) hosting; the HTTP mode is
    protected by an API key.

**Prerequisites / setup**

- Supported browser: Chromium-based browser with the Hyland Integrations Agent extension
  installed (MV3 side panel). [Confirm distribution channel.]
- A reachable UCEB environment and the BFF configured with the Hyland IAM OAuth client
  (client id/secret, authorize/token/end-session endpoints, required scopes:
  `openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp`).
- At least one registered System Configuration (CIC and/or OnBase) and a Solution
  Configuration for each line of business the customer uses (Salesforce, Workday).
- For Workday worker lookup, the BFF must be configured with Workday Staffing API
  credentials.
- The user must have the appropriate UCEB permissions for the content they access.
- [Supported versions: UCEB [x], Salesforce package [x], Workday tenant requirements [x].]

**Where it belongs in the docs**

- Installation / Configuration Guide: registering System Configurations, authoring Solution
  Configurations (mappings, upload document types, display columns, queries), IAM/OAuth
  setup, Workday Staffing API setup. [Link to existing docs.hyland.com topics.]
- Administrator Guide: ECM picker, choosing the active system, enabling IDP. [Link.]
- User Guide: signing in, browsing a record's documents, searching, uploading/capturing,
  viewing documents, using chat. [Link.]
- Module Reference Guide (MRG): [list the exact MRG topics that need new/updated entries].

**Supporting material**

- Architecture/flow diagrams (People → AI Agent via Chrome plugin → Hyland Content Services
  via MCP + UCEB). [Attach the slides / Figma.]
- Demo recording: [link].
- Design/Confluence page: [link].
- Screenshots: ECM picker, documents list, upload card, viewer, chat. [Attach.]

**Release notes needed**

[Yes / No]. The ticket currently indicates N — confirm with the writer whether this phase is
customer-releasable or internal-only.

**Notes for the writer**

- Please treat the chat examples above as representative, not exhaustive.
- Any in-progress "optimistic" document-list behavior (documents appearing immediately after
  upload, before the search index catches up) is a temporary UX measure and is expected to be
  reverted — please do **not** document it as a feature.
- [Call out anything from the issue description that did not ship this phase — for example if
  the iframe-vs-tab investigation was dropped, note the final decision here.]
