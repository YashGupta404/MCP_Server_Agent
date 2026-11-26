# ServiceNow Demo — Voiceover Prompt Pack (for ChatGPT / TTS)

> Important: ChatGPT cannot edit a video or burn audio onto it. Use it to generate the
> **script** below, turn that script into audio (ElevenLabs, Azure/Google TTS, or record your
> own voice), then add the audio track to your clip in any video editor (Clipchamp, CapCut,
> Premiere, DaVinci Resolve). The prompt in Part 1 makes ChatGPT produce an accurate script;
> Part 2 is a ready-to-use script you can narrate as-is.

---

## PART 1 — Prompt to paste into ChatGPT

You are a professional product-demo scriptwriter and voiceover copywriter. Write a clear,
confident, natural-sounding voiceover narration for a screen-recorded product demo video. The
product is the "Hyland Integrations Agent" (HIA) and this demo shows it working with
ServiceNow. I already have voiceovers for the Salesforce and Workday demos; keep the tone and
structure consistent with a polished enterprise software walkthrough.

Follow these rules:
- Tone: professional, calm, friendly, and concise. Enterprise software, not a hype ad.
- Use plain language. Say "Select" rather than "Click". Short sentences. Grade-9 reading level.
- Write for the ear: contractions are fine, avoid dense jargon, define an acronym once.
- Pace for roughly 130–150 words per minute. I will tell you the clip length; match it.
- Output the script in timed sections with approximate timestamps and a one-line note of what
  is on screen for each section, so I can sync narration to the recording.
- Do not invent features that aren't in the background below. If something is uncertain, keep
  the wording general rather than specific.
- End with a short, warm closing line.

Here is everything you need to know about the product and the ServiceNow flow:

BACKGROUND — WHAT THE HYLAND INTEGRATIONS AGENT IS
- It is a Chrome browser extension that opens as a side panel next to the business application
  the user is already working in (a "line of business" or LOB app). Supported LOBs: Salesforce,
  Workday, and ServiceNow.
- Its job: let users work with their enterprise documents and records — right inside the app —
  without switching tabs or learning APIs. Users can view, search, upload/capture, and open
  documents that are attached to the record on screen, and they can also just ask for things in
  plain language through a built-in chat.
- Behind the scenes it is powered by the Model Context Protocol (MCP) and Hyland's UCEB
  (Unified Content and Experience Backend), which connect to Hyland's content services. Every
  action runs as the signed-in user, so that person's permissions are always honored.
- Documents live in a Hyland content system (an ECM). The agent supports CIC (Hyland's native
  content system) and OnBase, and the user chooses which one to work against.

HOW IT WORKS FOR SERVICENOW SPECIFICALLY
- The user is inside ServiceNow, viewing a record such as a Change Request (the demo uses a
  Change Request; Incidents work the same way).
- The HIA side panel automatically detects the ServiceNow ticket on screen. It uses the ticket
  number to find the documents linked to that exact record, so the panel is always scoped to
  what the user is looking at.
- The documents list for a Change Request shows these columns: Document Name, Short Description,
  Change State, and Priority.
- Uploading a document: the user picks a document type (for example, "Change Request" document),
  fills in the metadata fields (Document Name, Short Description, and so on), optionally attaches
  a file, and uploads. The Document Name can auto-fill from the file name. Allowed file types
  include PDF, images (PNG, JPG, TIFF, BMP), Office files (DOCX, PPTX, XLSX), plus TXT and JSON.
  The file is sent securely and attached to the ServiceNow record.
- Searching: the user can run a saved search query to filter documents, or simply ask in chat.
- Viewing a document: when the user opens a document, it appears in the Hyland CIC Viewer,
  embedded right inside the side panel, so they never leave ServiceNow. They can also pop it out
  into a new browser tab.
- Chat: the user can type natural requests like "Show me the documents on this change request"
  or "Upload this file to this ticket," and the agent does it and replies with clickable links.
- Security: the agent acts as the signed-in user and respects that user's ServiceNow and content
  permissions.

THE DEMO LIKELY SHOWS, IN THIS ORDER (adapt to my actual recording):
1. A ServiceNow Change Request open in the browser.
2. Opening the Hyland Integrations Agent side panel (user already signed in).
3. The panel detecting the Change Request and listing its attached documents.
4. Searching or filtering the documents.
5. Uploading a new document: choosing a document type, filling metadata, attaching a file,
   and confirming it appears on the record.
6. Opening a document in the embedded CIC Viewer.
7. Optionally, asking the agent to do something in chat.
8. A short wrap-up.

Now write the voiceover script. First ask me one question: how long is my video (in seconds)?
Then produce the timed script to match that length.

---

## PART 2 — Ready-to-use narration script (edit to fit your clip)

Use this as-is, or shorten/lengthen per scene. Timestamps are a guide for a ~90-second clip.

[0:00–0:08] On screen: ServiceNow Change Request open in the browser.
"This is the Hyland Integrations Agent, working inside ServiceNow. Here we're looking at a
Change Request — and everything we need to do with its documents, we can do right here."

[0:08–0:18] On screen: the side panel opens next to the ServiceNow record.
"When we open the agent's side panel, it automatically recognizes the ticket we're on. No
searching, no switching tabs — it scopes straight to this Change Request."

[0:18–0:30] On screen: documents list populates with columns.
"It lists every document attached to this record, with the details that matter — document name,
short description, change state, and priority."

[0:30–0:42] On screen: user searches / filters the list.
"Need something specific? We can search and filter in place to find the right document in
seconds."

[0:42–0:58] On screen: upload flow — pick type, fill metadata, attach file.
"Adding a document is just as simple. We choose the document type, fill in the details — the
name can fill itself from the file — and attach the file. The agent sends it securely and
files it against this ServiceNow ticket."

[0:58–1:10] On screen: new document appears in the list.
"And there it is, attached to the record — no manual uploads into a separate system."

[1:10–1:22] On screen: open a document in the embedded CIC Viewer.
"To review a document, we open it right here in the viewer, embedded in the panel — so we never
leave ServiceNow."

[1:22–1:32] On screen: chat tab, user types a request.
"We can also just ask. In plain language, the agent can list documents, search, or capture a
file to this ticket — and it always acts as you, with your permissions."

[1:32–1:40] On screen: wrap / Hyland logo.
"That's the Hyland Integrations Agent with ServiceNow — your content, in context, right where
you work."

---

## PART 3 — Tips for a clean result
- Record the narration in short takes per scene; it's easier to re-do one line than the whole track.
- Leave a half-second of silence at the start and end of each line so it syncs cleanly.
- If using TTS, pick a calm, professional voice and set speed slightly below default.
- Match the script timing to your real clip — tell ChatGPT the exact seconds and ask it to retime.
- Keep terminology consistent with your Salesforce/Workday demos (same product name, same tone).
