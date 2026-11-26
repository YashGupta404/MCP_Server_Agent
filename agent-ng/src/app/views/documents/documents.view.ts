import { ChangeDetectorRef, Component, ElementRef, HostListener, NgZone, OnDestroy, OnInit, TemplateRef, ViewChild } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarRef, TextOnlySnackBar } from '@angular/material/snack-bar';
import { DomSanitizer, SafeHtml, SafeResourceUrl } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { AgentAuthService } from '../../services/auth/agent-auth.service';
import { BffApiService } from '../../services/bff/bff-api.service';
import { ChatService } from '../../services/chat.service';
import { ContextDetectorService } from '../../services/context-detector.service';
import { DocTypeField, DocumentService, IdpClassifyResult, QueryInfo, QueryInput, SelfWorker, WorkerMatch } from '../../services/document.service';
import { Settings } from '../../services/settings';
import { SystemConfigService } from '../../services/system-config.service';
import { ThemeService } from '../../services/theme.service';
import { RecordContext } from '../../types/record-context.model';
import { UcebDocument } from '../../types/uceb-document.model';
import { renderMarkdown } from '../../utils/markdown';

const ext: any = (globalThis as any).chrome ?? (globalThis as any).browser;

@Component({
  selector: 'agent-documents',
  templateUrl: './documents.view.html',
  styleUrls: ['./documents.view.scss'],
  standalone: false,
})
export class DocumentsView implements OnInit, OnDestroy {
  profileName = '';
  profileEmail = '';
  profileInitials = '--';
  tab: 'documents' | 'chat' = 'documents';
  loading = true;
  error = '';
  context: RecordContext | null = null;
  activeSystem = '';
  systemCollapsed = false;
  documents: UcebDocument[] = [];
  columns: string[] | null = null;
  // ---- Workday worker resolution / self-scoping ----
  selfWorker: SelfWorker | null = null;
  notAuthorized = false;
  notAuthorizedWho = '';
  resolveStatus = '';
  workerMatches: WorkerMatch[] = [];
  resolving = false;
  private loadSeq = 0;
  private navTabUrl = '';
  search = '';
  page = 1;
  pageSize = 7;
  pageSizeOptions = [5, 7, 10, 25];
  menuFor = '';
  menuDoc: UcebDocument | null = null;
  menuTop = 0;
  menuLeft = 0;
  // ---- upload card state ----
  docTypes: string[] = [];
  private docTypesBoType: string | null = null;
  // Which upload file the values in docTypeFieldValues belong to.
  private metaValuesFor: string | null = null;
  docTypesLoading = false;
  docTypesError = '';
  selectedDocType = '';
  docTypeFields: DocTypeField[] = [];
  docTypeFieldsLoading = false;
  docTypeFieldsError = '';
  // Metadata fields are keyed by type+record so selecting a type (or IDP auto-selecting one) reuses an
  // already-loaded list instead of re-reading the solution config each time.
  private docTypeFieldsCache: Record<string, DocTypeField[]> = {};
  private docTypeFieldsInFlight: Record<string, Promise<DocTypeField[]>> = {};
  docTypeFieldValues: Record<string, string> = {};
  idpEnabled = Boolean(Settings.config?.idpEnabled);
  idpConnected = false;
  idpPublicHost = false;
  idpBusy = false;
  selectedUploadName: string | null = null;
  fileClassifications: Record<string, {
    docType: string | null;
    confidence: number;
    reviewRequired: boolean;
    error?: string;
    fields?: Array<{ id: string; name?: string; value?: string; confidence?: number; reviewRequired?: boolean; importable?: boolean }>;
    uploadable?: boolean;
    edited?: boolean;
    autoDocType?: string | null;
  }> = {};
  private uploadMetaSnapshots: Record<string, Record<string, Record<string, string>>> = {};
  uploadFiles: File[] = [];
  dragOver = false;
  uploading = false;
  uploadStatus = '';
  uploadError = false;
  private unsub?: () => void;
  private debounce: any;
  private actionToastTimer: any;

  constructor(
    private readonly auth: AgentAuthService,
    private readonly bff: BffApiService,
    private readonly ctxSvc: ContextDetectorService,
    private readonly docSvc: DocumentService,
    private readonly systems: SystemConfigService,
    private readonly router: Router,
    private readonly zone: NgZone,
    private readonly sanitizer: DomSanitizer,
    private readonly chat: ChatService,
    readonly theme: ThemeService,
    private readonly dialog: MatDialog,
    private readonly snackBar: MatSnackBar,
    private readonly translate: TranslateService,
    private readonly cdr: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> {
    void this.loadProfile();
    // React to navigation / tab switches so the record + doclist stay contextual.
    this.unsub = this.ctxSvc.onChange(() => this.zone.run(() => this.scheduleContextCheck()));
    // Resolve the record once up front so the document list and the upload doc-type list load in
    // PARALLEL (like the old popup) instead of serially. Previously doc types only began loading after
    // systems.list() AND the whole document load finished, so the dropdown sat on "Loading document
    // types…" far longer than before.
    const ctx = await this.resolveContext();
    if (ctx?.businessObjectId && ctx?.businessObjectType) this.context = ctx;
    // The active-system name only feeds the header, so don't let it block the record/doc-type loads.
    void this.systems
      .list()
      .then(({ active }) => {
        this.activeSystem = active;
        this.cdr.detectChanges();
      })
      .catch(() => {});
    await Promise.all([this.reload(), this.loadDocTypes()]);
    if (this.idpEnabled) this.loadIdpStatus();
  }

  private async loadProfile(): Promise<void> {
    try {
      const profile = await this.bff.getProfile();
      this.profileName = profile.name || '';
      this.profileEmail = profile.email || '';
      this.profileInitials = profile.initials || '--';
      this.cdr.detectChanges();
    } catch {
      // Non-fatal: keep the header usable even if the profile call fails.
    }
  }

  async signOut(): Promise<void> {
    await this.auth.clearSession();
    await this.router.navigate(['/sign-in']);
  }

  ngOnDestroy(): void {
    this.unsub?.();
    clearTimeout(this.debounce);
    clearTimeout(this.viewerWatchdog);
    clearTimeout(this.viewerLoadingTimer);
    clearTimeout(this.actionToastTimer);
  }

  private scheduleContextCheck(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => this.checkContextChanged(), 350);
  }

  private async checkContextChanged(): Promise<void> {
    const ctx = await this.ctxSvc.getActiveContext();
    // Did the active tab navigate to a different page (e.g. another worker, or back/forward)?
    const url = this.ctxSvc.lastTabUrl;
    const navigated = !!url && url !== this.navTabUrl;
    this.navTabUrl = url;
    // React to real records AND Workday worker-resolution / not-authorised pages; ignore transient nulls.
    if (!ctx?.businessObjectId && !ctx?.needsResolve && !ctx?.notAuthorized) {
      // On a fresh Workday navigation the context may not have resolved yet (lazy Employee ID) — show the
      // "Detecting…" spinner so the previous record/403 doesn't linger; reload fires on the next publish.
      if (navigated && /\.(workday|myworkday)\.com/i.test(url)) {
        this.zone.run(() => {
          this.loading = true;
          this.notAuthorized = false;
          this.workerMatches = [];
          this.resolveStatus = this.translate.instant('documents.detecting');
          this.cdr.detectChanges();
        });
      }
      return;
    }
    const key = (c: RecordContext | null) =>
      `${c?.businessObjectType || ''}:${c?.businessObjectId || c?.resolveQuery || c?.displayName || ''}:${c?.needsResolve ? 'r' : ''}${c?.notAuthorized ? 'n' : ''}`;
    const changed = key(ctx) !== key(this.context);
    const emptyRetry =
      !this.loading && !this.error && this.documents.length === 0 && !this.notAuthorized && !this.workerMatches.length;
    // Always reload on a real navigation (even back to the SAME worker) so documents auto-refresh
    // without needing a manual refresh click.
    if (changed || emptyRetry || navigated) {
      await this.reload();
    }
  }

  async reload(): Promise<void> {
    const token = ++this.loadSeq;
    this.loading = true;
    this.error = '';
    this.menuFor = '';
    this.menuDoc = null;
    this.notAuthorized = false;
    this.resolveStatus = this.translate.instant('documents.detecting');
    this.workerMatches = [];
    try {
      const ctx = await this.resolveContext();
      if (token !== this.loadSeq) return;

      // Workday: resolve the signed-in worker ("self") once so chat can scope to the logged-in user.
      if (this.isWorkdayContext(ctx) && !this.selfWorker) {
        try {
          this.selfWorker = await this.docSvc.fetchMe();
        } catch {
          /* non-fatal: chat falls back to the on-screen record */
        }
        if (token !== this.loadSeq) return;
      }

      // Workday peer / your own manager: name but no Employee ID -> not authorised. A subordinate's
      // Employee ID can render a beat late, so re-check briefly before committing to the 403.
      if (ctx?.notAuthorized) {
        this.context = ctx;
        this.documents = [];
        this.columns = null;
        this.recompute();
        // If we already know the Manager and it isn't the signed-in user, this is a peer/superior -
        // deny immediately (no probe, no grace wait). But never deny while the detector is still
        // auto-scrolling to hydrate a lazy Employee ID — hold the loading state instead.
        if (!ctx.probing && ctx.managerName && !this.isAuthorizedForWorker(ctx)) {
          this.resolveStatus = '';
          this.notAuthorized = true;
          this.notAuthorizedWho = ctx.displayName || 'this employee';
          return;
        }
        // Kick a fresh auto-scroll probe (resets its budget) so a lazy Employee ID re-hydrates on this
        // reload/Refresh, then show "checking access" while the grace re-check waits for it.
        void this.ctxSvc.reprobe();
        // Show a clear "checking access" state (not the 403) while Workday lazy-renders the Employee ID.
        this.resolveStatus = this.translate.instant('documents.worker.checking', { who: ctx.displayName || 'this worker' });
        const flipped = await this.graceRecheckNotAuthorized(token);
        if (token !== this.loadSeq) return;
        this.resolveStatus = '';
        if (flipped) {
          await this.reload();
          return;
        }
        this.notAuthorized = true;
        this.notAuthorizedWho = ctx.displayName || 'this employee';
        return;
      }

      // Workday worker profile with no WID in the URL: resolve the scraped name/Employee ID -> WID.
      if (ctx?.needsResolve && ctx.resolveQuery) {
        // Employee-ID visibility alone is NOT authorisation — Workday shows Employee IDs for superiors
        // too. Only self + your own reports (Manager == signed-in user) may be viewed.
        if (!this.isAuthorizedForWorker(ctx)) {
          this.context = ctx;
          this.documents = [];
          this.columns = null;
          this.recompute();
          this.resolveStatus = '';
          this.notAuthorized = true;
          this.notAuthorizedWho = ctx.displayName || 'this employee';
          return;
        }
        this.context = ctx;
        this.documents = [];
        this.columns = null;
        this.recompute();
        await this.resolveAndLoadWorker(ctx.resolveQuery, token);
        return;
      }

      // Never wipe an already-loaded list when the page momentarily reports no record
      // (Salesforce fires many navigation events; a transient null must not blank the table).
      if (!ctx?.businessObjectId || !ctx?.businessObjectType) {
        this.resolveStatus = '';
        if (!this.context?.businessObjectId) {
          this.documents = [];
          this.columns = null;
          this.recompute();
        }
        return;
      }
      this.context = ctx;
      // Doc types may have loaded before the record was detected (unscoped list) - rescope them.
      if (ctx.businessObjectType !== this.docTypesBoType) void this.loadDocTypes();
      const { documents, columns } = await this.docSvc.listForContext({
        businessObjectType: ctx.businessObjectType,
        businessObjectId: ctx.businessObjectId,
      });
      if (token !== this.loadSeq) return;
      this.resolveStatus = '';
      this.documents = documents;
      this.columns = columns;
      this.page = 1;
      this.recompute();
      // Prefetch the saved queries in the background (fire-and-forget), like the old popup did during
      // context load, so the Query panel's dropdown is already populated when opened instead of sitting
      // on "Loading…" while the slow get_solution_configurations call runs. A different record type
      // drops the stale selection so the refetch reflects the new object's queries.
      if (this.queriesBoType && this.queriesBoType !== ctx.businessObjectType) {
        this.queriesLoaded = false;
        this.queries = [];
        this.selectedQueryId = '';
        this.queryInputs = [];
        this.queryActive = false;
      }
      void this.loadQueries();
    } catch (e: any) {
      this.error = e?.message || 'Could not load documents.';
    } finally {
      // Async continuations can land outside Angular's zone — force a tick so the table renders.
      this.zone.run(() => {
        this.loading = false;
        this.cdr.detectChanges();
      });
    }
  }

  changeSystem(): void {
    this.router.navigate(['/systems']);
  }

  get ctxDebug(): string {
    return this.ctxSvc.lastDebug;
  }

  // The content script / background may still be warming up (or the LOB page may be lazy-rendering the
  // worker name) when the panel opens; retry for a few seconds while the loading spinner shows.
  private async resolveContext(): Promise<RecordContext | null> {
    for (let i = 0; i < 8; i++) {
      const ctx = await this.ctxSvc.getActiveContext();
      if (ctx?.businessObjectId || ctx?.needsResolve || ctx?.notAuthorized) return ctx;
      await new Promise((r) => setTimeout(r, 400));
    }
    return this.ctxSvc.getActiveContext();
  }

  isNameCol(col: string): boolean {
    const c = (col || '').toLowerCase();
    return c === 'document name' || c === 'name' || c === 'hfs_name';
  }

  fileExt(doc: UcebDocument): string {
    const m = /\.([a-z0-9]+)$/i.exec(doc.name || '');
    return m ? m[1].toLowerCase() : '';
  }

  fileKind(doc: UcebDocument): string {
    const e = this.fileExt(doc);
    if (e === 'pdf') return 'pdf';
    if (['doc', 'docx'].includes(e)) return 'doc';
    if (['xls', 'xlsx', 'csv'].includes(e)) return 'xls';
    if (['tif', 'tiff', 'png', 'jpg', 'jpeg', 'gif', 'bmp'].includes(e)) return 'img';
    if (e === 'txt') return 'txt';
    return 'file';
  }

  // Maps the file kind to a Satori icon name for the document-type badge.
  fileIconName(doc: UcebDocument): string {
    switch (this.fileKind(doc)) {
      case 'pdf':
        return 'pdf';
      case 'doc':
        return 'document';
      case 'xls':
        return 'excel';
      case 'img':
        return 'image';
      case 'txt':
        return 'note';
      default:
        return 'document';
    }
  }

  close(): void {
    window.close();
  }

  // ---- filtering / columns / cells (ported from the old popup) ----
  // NOTE: these are STORED fields (not getters) recomputed by recompute() only when the data,
  // search text or page changes. Binding *ngFor to a getter that returns a fresh array every
  // change-detection pass made the list flicker/vanish under the Salesforce page's constant CD.
  filtered: UcebDocument[] = [];
  displayColumns: string[] = [];
  paged: UcebDocument[] = [];
  totalPages = 1;

  private recompute(): void {
    const q = this.search.trim().toLowerCase();
    this.filtered = !q
      ? this.documents
      : this.documents.filter((d) => {
          const bits: Array<string | undefined> = [d.name, d.type, d.docId];
          if (d.attributes) for (const [k, v] of Object.entries(d.attributes)) bits.push(k, v);
          return bits.filter(Boolean).join(' ').toLowerCase().includes(q);
        });
    this.totalPages = Math.max(1, Math.ceil(this.filtered.length / this.pageSize));
    if (this.page > this.totalPages) this.page = this.totalPages;
    const start = (this.page - 1) * this.pageSize;
    this.paged = this.filtered.slice(start, start + this.pageSize);
    this.displayColumns = this.computeColumns();
  }

  private computeColumns(): string[] {
    if (this.columns && this.columns.length) return this.columns;
    const cols: string[] = [];
    const seen = new Set<string>();
    let hasType = false;
    for (const d of this.documents) {
      if (d.type) hasType = true;
      if (d.attributes) {
        for (const k of Object.keys(d.attributes)) {
          const key = k.toLowerCase();
          if (!seen.has(key)) {
            seen.add(key);
            cols.push(k);
          }
        }
      }
    }
    if (hasType && !seen.has('document type')) cols.push('Document Type');
    return cols.length ? cols : ['Document Name', 'Document Type'];
  }

  trackByDocId(_i: number, d: UcebDocument): string {
    return d.docId;
  }

  trackByCol(_i: number, c: string): string {
    return c;
  }

  trackByDocTypeField(_i: number, field: DocTypeField): string {
    return field.id;
  }

  cell(doc: UcebDocument, col: string): string {
    const c = (col || '').toLowerCase();
    if (c === 'document type' || c === 'documenttypename' || c === 'type') return doc.type || '';
    if (c === 'hfs_name' || c === 'document name' || c === 'name') return doc.name || '';
    let v = '';
    if (doc.attributes) {
      const k = Object.keys(doc.attributes).find((x) => x.toLowerCase() === c);
      if (k) v = doc.attributes[k] ?? '';
    }
    const iso = /^(\d{4}-\d{2}-\d{2})T/.exec(v);
    return iso ? iso[1] : v;
  }

  // ---- pagination ----

  rangeLabel(): string {
    const total = this.filtered.length;
    if (!total) return '0 of 0';
    const start = (this.page - 1) * this.pageSize + 1;
    const end = Math.min(this.page * this.pageSize, total);
    return `${start} \u2013 ${end} of ${total}`;
  }

  onSearch(): void {
    this.page = 1;
    this.recompute();
  }

  prev(): void {
    if (this.page > 1) {
      this.page--;
      this.recompute();
    }
  }

  next(): void {
    if (this.page < this.totalPages) {
      this.page++;
      this.recompute();
    }
  }

  onPageSizeChange(): void {
    this.page = 1;
    this.recompute();
  }

  // ---- server-side Query (keyword search), ported from popup.js ----
  queryOpen = false;
  queries: QueryInfo[] = [];
  queriesLoading = false;
  queriesLoaded = false;
  // The business object the current query list was loaded (or is loading) for, plus the in-flight
  // fetch, so the background prefetch in reload() and opening the panel never double round-trip.
  private queriesBoType = '';
  private queriesInFlight: Promise<void> | null = null;
  selectedQueryId = '';
  queryInputs: QueryInput[] = [];
  queryValues: Record<string, string> = {};
  queryOps: Record<string, string> = {};
  queryStatus = '';
  queryStatusError = false;
  querySearching = false;
  queryFieldsLoading = false;
  queryActive = false;
  private queryMetaCache: Record<string, { inputs: QueryInput[]; cols: string[] }> = {};
  // In-flight metadata fetches, so the background prefetch and a user selecting a query reuse the
  // same request instead of each firing a slow get_query_metadata round trip.
  private queryMetaInFlight: Record<string, Promise<{ inputs: QueryInput[]; cols: string[] }>> = {};

  async toggleQuery(): Promise<void> {
    this.queryOpen = !this.queryOpen;
    if (this.queryOpen) {
      // Usually already prefetched by reload(); await the same in-flight promise if it isn't done yet.
      await this.loadQueries();
      return;
    }
    // Closing the panel reverts to the full record list (and resets the query form).
    if (this.queryActive) {
      await this.clearQuery();
    } else {
      this.selectedQueryId = '';
      this.queryInputs = [];
      this.queryStatus = '';
      this.queryStatusError = false;
    }
  }

  // Loads the saved queries configured for the current business object, ONCE per object. Safe to call
  // eagerly (background prefetch) and again when the panel opens: it returns the already-loaded result
  // or the in-flight fetch instead of issuing a second slow get_solution_configurations round trip.
  private loadQueries(): Promise<void> {
    const boType = this.context?.businessObjectType || '';
    if (this.queriesLoaded && this.queriesBoType === boType) return Promise.resolve();
    if (this.queriesInFlight && this.queriesBoType === boType) return this.queriesInFlight;

    this.queriesBoType = boType;
    this.queriesLoading = true;
    this.queryStatus = '';
    this.queryStatusError = false;
    this.queriesInFlight = (async () => {
      try {
        this.queries = await this.docSvc.fetchQueries(boType);
        this.queriesLoaded = true;
        // Warm only the DEFAULT (or first) query's fields. Prefetching every query fired a burst of
        // get_query_metadata calls that queued on the same MCP→UCEB→OnBase pipeline a user search uses,
        // making the search slow. Other queries load on selection (then cached).
        const primary = this.queries.find((q) => (q as any).default) ?? this.queries[0];
        if (primary?.id) void this.ensureQueryMeta(primary.id).catch(() => {});
      } catch (e: any) {
        this.queryStatus = e?.message || 'Could not load queries.';
        this.queryStatusError = true;
      } finally {
        this.queriesLoading = false;
        this.queriesInFlight = null;
      }
    })();
    return this.queriesInFlight;
  }

  // Returns a query's search-field metadata, loading it once and reusing any in-flight fetch.
  private ensureQueryMeta(qid: string): Promise<{ inputs: QueryInput[]; cols: string[] }> {
    const cached = this.queryMetaCache[qid];
    if (cached) return Promise.resolve(cached);
    const inFlight = this.queryMetaInFlight[qid];
    if (inFlight) return inFlight;
    const p = this.docSvc
      .fetchQueryMetadata(qid)
      .then((meta) => {
        const entry = {
          inputs: meta.inputs || [],
          cols: (meta.resultColumns || []).map((c) => c.name || '').filter(Boolean),
        };
        this.queryMetaCache[qid] = entry;
        return entry;
      })
      .finally(() => {
        delete this.queryMetaInFlight[qid];
      });
    this.queryMetaInFlight[qid] = p;
    return p;
  }

  async onQueryChange(): Promise<void> {
    this.queryInputs = [];
    this.queryValues = {};
    this.queryOps = {};
    this.queryStatus = '';
    this.queryStatusError = false;
    const qid = this.selectedQueryId;
    if (!qid) return;
    // Instant when the field metadata was already prefetched in the background by loadQueries().
    const ready = this.queryMetaCache[qid];
    this.queryFieldsLoading = !ready;
    try {
      const cached = ready ?? (await this.ensureQueryMeta(qid));
      // The user may have switched queries while this was loading — ignore a stale result.
      if (this.selectedQueryId !== qid) return;
      this.queryInputs = cached.inputs;
      for (const inp of this.queryInputs) {
        this.queryOps[inp.id] = this.operatorsFor(inp)[0];
        this.queryValues[inp.id] = '';
      }
    } catch (e: any) {
      this.queryStatus = e?.message || 'Could not load query fields.';
      this.queryStatusError = true;
    } finally {
      this.queryFieldsLoading = false;
    }
  }

  operatorsFor(inp: QueryInput): string[] {
    return inp.supportedOperators && inp.supportedOperators.length ? inp.supportedOperators : ['Contains'];
  }

  friendlyOperator(op: string): string {
    const map: Record<string, string> = {
      EqualsCaseInsensitive: 'Equals',
      EqualsCaseSensitive: 'Equals (exact)',
      ContainsCaseInsensitive: 'Contains',
      ContainsCaseSensitive: 'Contains (exact)',
      StartsWithCaseInsensitive: 'Starts with',
      StartsWithCaseSensitive: 'Starts with (exact)',
      Equals: 'Equals',
      Contains: 'Contains',
      StartsWith: 'Starts with',
    };
    return map[op] || op.replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  // First filled input wins (single-field search, same as the old popup), with required/minLength checks.
  private collectQueryFilter(): { filterFieldId?: string; filterValue?: string; filterOperator?: string; error?: string } {
    for (const inp of this.queryInputs) {
      const value = (this.queryValues[inp.id] || '').trim();
      const label = inp.name || inp.id;
      if (value) {
        if (inp.minLength && value.length < inp.minLength) {
          return { error: `${label}: entry is too short (min ${inp.minLength}).` };
        }
        return { filterFieldId: inp.id, filterValue: value, filterOperator: this.queryOps[inp.id] || 'Contains' };
      }
      if (inp.required) return { error: `${label} is required.` };
    }
    return { error: 'Enter a search value.' };
  }

  async runQuery(): Promise<void> {
    if (!this.selectedQueryId) {
      this.queryStatus = 'Select a query first.';
      this.queryStatusError = true;
      return;
    }

    // CFS saved queries ("*_CFS") can't run via queries/execute (UCEB 500s on CFS). Each maps 1:1 to a
    // business-object type, so list that object's documents directly via the working list endpoint.
    const selected = this.queries.find((q) => q.id === this.selectedQueryId);
    if (/_CFS$/i.test((selected as any)?.name || '')) {
      const objInput = this.queryInputs.find((i) => /object id\s*$/i.test(i.name || ''));
      const objVal = objInput ? (this.queryValues[objInput.id] || '').trim() : '';
      if (!objInput || !objVal) {
        this.queryStatus = `Enter the ${objInput?.name || 'Object ID'} to search.`;
        this.queryStatusError = true;
        return;
      }
      const boType = (objInput.name || '').replace(/\s*object id\s*$/i, '').replace(/\s+/g, '').toLowerCase();
      this.querySearching = true;
      this.queryStatus = 'Searching\u2026';
      this.queryStatusError = false;
      try {
        const { documents, columns } = await this.docSvc.listForContext({
          businessObjectType: boType,
          businessObjectId: objVal,
          direct: true,
        });
        const cols = this.queryMetaCache[this.selectedQueryId]?.cols;
        this.documents = documents;
        this.columns = cols && cols.length ? cols : columns;
        this.page = 1;
        this.recompute();
        this.queryActive = true;
        this.queryStatus = `${documents.length} result(s).`;
        this.queryStatusError = false;
      } catch (e: any) {
        this.queryStatus = e?.message || 'Query failed.';
        this.queryStatusError = true;
      } finally {
        this.querySearching = false;
        // listForContext resolves off Angular's zone (fetch), so repaint the results now.
        this.zone.run(() => this.cdr.detectChanges());
      }
      return;
    }

    const filter = this.collectQueryFilter();
    if (filter.error) {
      this.queryStatus = filter.error;
      this.queryStatusError = true;
      return;
    }
    this.querySearching = true;
    this.queryStatus = 'Searching\u2026';
    this.queryStatusError = false;
    try {
      const documents = await this.docSvc.executeQuery({
        businessObjectType: this.context?.businessObjectType || '',
        queryId: this.selectedQueryId,
        businessObjectId: this.context?.businessObjectId || null,
        filterFieldId: filter.filterFieldId,
        filterValue: filter.filterValue,
        filterOperator: filter.filterOperator,
      });
      const cols = this.queryMetaCache[this.selectedQueryId]?.cols;
      this.documents = documents;
      this.columns = cols && cols.length ? cols : null;
      this.page = 1;
      this.recompute();
      this.queryActive = true;
      this.queryStatus = `${documents.length} result(s).`;
      this.queryStatusError = false;
    } catch (e: any) {
      this.queryStatus = e?.message || 'Query failed.';
      this.queryStatusError = true;
    } finally {
      this.querySearching = false;
      // executeQuery resolves off Angular's zone (fetch), so repaint the results now.
      this.zone.run(() => this.cdr.detectChanges());
    }
  }

  // Back to the default record list.
  async clearQuery(): Promise<void> {
    this.selectedQueryId = '';
    this.queryInputs = [];
    this.queryValues = {};
    this.queryOps = {};
    this.queryStatus = '';
    this.queryStatusError = false;
    this.queryActive = false;
    await this.reload();
  }

  // ---- Talk with agent (BFF /api/chat -> Agent Builder -> dev tunnel -> MCP) ----
  @ViewChild('chatLog') private chatLogRef?: ElementRef<HTMLDivElement>;
  chatTurns: Array<{ role: 'user' | 'agent' | 'error'; text: string; html?: SafeHtml; files?: string[]; docs?: Array<{ docId: string; name: string }> }> = [];
  chatInput = '';
  chatSending = false;
  chatFiles: File[] = [];

  quickPrompt(text: string): void {
    if (this.chatSending) return;
    this.chatInput = text;
    this.sendChat();
  }

  onChatFilePick(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    if (input.files?.length) this.chatFiles = this.chatFiles.concat(Array.from(input.files));
    input.value = '';
  }

  removeChatFile(i: number): void {
    this.chatFiles.splice(i, 1);
  }

  // "show/list/see my documents" etc. -> render the real list deterministically (not via the LLM).
  // Excludes the search chip ("I want to search a document" has no show/list verb).
  private isListDocsIntent(text: string): boolean {
    const t = (text || '').toLowerCase().trim();
    return /\b(show|list|see|view|display|get|fetch)\b/.test(t) && /\bdoc(ument)?s?\b/.test(t);
  }

  async sendChat(): Promise<void> {
    const text = this.chatInput.trim();
    const files = this.chatFiles;
    if (this.chatSending || (!text && !files.length)) return;
    this.chatTurns.push({ role: 'user', text: text || '(no message)', files: files.map((f) => f.name) });
    this.chatInput = '';
    this.chatFiles = [];
    this.chatSending = true;
    this.scrollChat();
    try {
      const attachments = await Promise.all(files.map((f) => this.fileToAttachment(f)));
      const chatContext = this.chatScopeContext();
      // Deterministic fast-path: "show my documents" renders the REAL list (all of them, with Open
      // buttons) instantly, instead of the slow LLM — which also truncates/caps long lists and can hit
      // the agent's output limit. Non-listing questions still go to the agent.
      if (!files.length && this.isListDocsIntent(text) && chatContext?.businessObjectId && chatContext?.businessObjectType) {
        const { documents } = await this.docSvc.listForContext({
          businessObjectType: chatContext.businessObjectType,
          businessObjectId: chatContext.businessObjectId,
          direct: true,
        });
        const who = chatContext.displayName || 'this record';
        const n = documents.length;
        const msg = n
          ? `Here ${n === 1 ? 'is' : 'are'} ${n} document${n === 1 ? '' : 's'} for ${who}:`
          : `No documents found for ${who}.`;
        const docs = n ? documents.map((d) => ({ docId: d.docId, name: d.name || d.docId })) : undefined;
        // The BFF call lands outside Angular's zone, so render the turn inside it or it won't show.
        this.zone.run(() => {
          this.chatTurns.push({
            role: 'agent',
            text: msg,
            html: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(msg)),
            docs,
          });
          this.chatSending = false;
          this.cdr.detectChanges();
        });
        this.scrollChat();
        return;
      }
      const reply = await this.chat.send(this.buildOutgoing(text, chatContext), attachments, chatContext);
      const { clean, docs } = this.extractChatDocs(reply);
      this.zone.run(() => {
        this.chatTurns.push({
          role: 'agent',
          text: clean,
          html: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(clean)),
          docs: docs.length ? docs : undefined,
        });
        this.cdr.detectChanges();
      });
      this.openViewerFromReply(reply);
      if (this.context?.businessObjectId && files.length) await this.reload();
    } catch (e: any) {
      this.zone.run(() => {
        this.chatTurns.push({ role: 'error', text: 'Error: ' + (e?.message || 'Agent call failed.') });
        this.cdr.detectChanges();
      });
    } finally {
      this.zone.run(() => {
        this.chatSending = false;
        this.cdr.detectChanges();
      });
      this.scrollChat();
    }
  }

  // Workday: the chatbot answers as the SIGNED-IN worker (self), even when the panel shows a
  // subordinate. Other LOBs use the on-screen record.
  private chatScopeContext(): RecordContext | null {
    if (this.isWorkdayContext(this.context) && this.selfWorker?.wid) {      return {
        businessObjectType: 'employee',
        businessObjectId: this.selfWorker.wid,
        displayName: this.selfWorker.name || `employee ${this.selfWorker.wid}`,
        source: 'workday',
      };
    }
    return this.context;
  }

  // Append the on-screen record as a hint so "this record" resolves without the user typing an id.
  private buildOutgoing(text: string, c: RecordContext | null = this.context): string {
    if (!c?.businessObjectId) return text;
    const hint =
      `\n\n[Context \u2014 the user is viewing this record in the browser: ` +
      `businessObjectType=${c.businessObjectType}, businessObjectId=${c.businessObjectId}. ` +
      `Use these when listing or uploading documents for "this record".]`;
    return (text + hint).trim();
  }

  // If the agent's reply contains a Hyland viewer link, also open it in the in-panel viewer.
  private openViewerFromReply(reply: string): void {    if (typeof reply !== 'string') return;
    const urls = reply.match(/https?:\/\/[^\s)"'\]]+/gi) || [];
    const viewer = urls
      .map((u) => u.replace(/[.,);\]]+$/, ''))
      .find((u) => /cic-viewer|\/#\/(?:documents|cfs)\//i.test(u));
    if (!viewer) return;
    const idMatch = reply.match(/document\s+([A-Za-z0-9_-]+)/i);
    this.openViewer(viewer, idMatch ? `Document ${idMatch[1]}` : 'Document');
  }

  // Pulls the [[doc:<docId>|<name>]] markers the agent appends per listed document and strips them
  // from the visible text, so the chat can render an "Open" button that shows each doc in the panel.
  private extractChatDocs(reply: string): { clean: string; docs: Array<{ docId: string; name: string }> } {
    const docs: Array<{ docId: string; name: string }> = [];
    const seen = new Set<string>();
    const clean = (reply || '').replace(/\[\[doc:\s*([^|\]]+?)\s*(?:\|\s*([^\]]*?))?\s*\]\]/gi, (_m, id, name) => {
      const docId = (id || '').trim();
      if (docId && !seen.has(docId)) {
        seen.add(docId);
        docs.push({ docId, name: (name || '').trim() || docId });
      }
      return '';
    });
    return { clean: clean.replace(/[ \t]+$/gm, '').replace(/\n{3,}/g, '\n\n').trim(), docs };
  }

  // Opens a document the chatbot listed in the in-panel viewer (same path as a Documents-tab row).
  openChatDoc(d: { docId: string; name: string }): void {
    void this.preview({ docId: d.docId, name: d.name });
  }

  private scrollChat(): void {
    setTimeout(() => {
      const el = this.chatLogRef?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  private async fileToAttachment(file: File): Promise<{ name: string; mime: string; dataBase64: string }> {
    const buffer = await file.arrayBuffer();
    const bytes = new Uint8Array(buffer);
    const chunk = 0x8000;
    let binary = '';
    for (let i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, Array.from(bytes.subarray(i, i + chunk)));
    }
    return { name: file.name, mime: file.type || 'application/octet-stream', dataBase64: btoa(binary) };
  }

  // ---- row menu (actions wired in the next step) ----

  toggleMenu(doc: UcebDocument, ev: Event): void {
    ev.stopPropagation();
    const mouse = ev as MouseEvent;
    const rect = (ev.currentTarget as HTMLElement | null)?.getBoundingClientRect();
    const isSame = this.menuFor === doc.docId;
    if (isSame) {
      this.menuFor = '';
      this.menuDoc = null;
      return;
    }

    const menuWidth = 148;
    const leftFromButton = rect ? rect.right - menuWidth : mouse.clientX - menuWidth + 12;
    const topFromButton = rect ? rect.bottom + 6 : mouse.clientY + 6;
    this.menuLeft = Math.max(12, Math.min(leftFromButton, window.innerWidth - menuWidth - 12));
    this.menuTop = Math.max(12, topFromButton);
    this.menuFor = doc.docId;
    this.menuDoc = doc;
  }

  @HostListener('document:click')
  closeMenu(): void {
    this.menuFor = '';
    this.menuDoc = null;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.viewerOpen) this.closeViewer();
  }

  // ---- in-panel viewer (ported from popup.js openDocumentPreview/openViewer) ----
  viewerOpen = false;
  viewerTitle = '';
  viewerUrl = '';
  viewerSafeUrl: SafeResourceUrl | null = null;
  viewerLoading = false;
  viewerFallback = false;
  viewerError = '';
  private viewerWatchdog: any;
  private viewerLoadingTimer: any;

  // Clicking a document row opens it in the in-panel CIC viewer, exactly like the old plugin:
  // resolve the viewer URL from the MCP, then embed it in the overlay iframe.
  async preview(doc: UcebDocument): Promise<void> {
    this.menuFor = '';
    clearTimeout(this.viewerWatchdog);
    this.viewerOpen = true;
    this.viewerTitle = doc.name || doc.docId;
    this.viewerUrl = '';
    this.viewerSafeUrl = null;
    this.viewerFallback = false;
    this.viewerError = '';
    this.viewerLoading = true;
    try {
      const url = await this.docSvc.openInViewer(doc.docId);
      this.openViewer(url, doc.name || doc.docId);
    } catch (e: any) {
      this.viewerLoading = false;
      this.viewerFallback = true;
      this.viewerError = e?.message || 'Could not open the viewer for this document.';
    }
  }

  private openViewer(url: string, title: string): void {
    clearTimeout(this.viewerWatchdog);
    clearTimeout(this.viewerLoadingTimer);
    this.viewerUrl = url;
    this.viewerSafeUrl = this.sanitizer.bypassSecurityTrustResourceUrl(url);
    this.viewerTitle = title || 'Document';
    this.viewerFallback = false;
    this.viewerLoading = true;
    // A cross-origin iframe doing login redirects fires 'load' unreliably, so don't gate the overlay
    // on it — clear it shortly so the embedded CIC page shows automatically (no extra click needed).
    this.viewerLoadingTimer = setTimeout(
      () =>
        this.zone.run(() => {
          this.viewerLoading = false;
          this.cdr.detectChanges();
        }),
      1200,
    );
    // A real framing block never renders; after a generous wait offer the fallback.
    this.viewerWatchdog = setTimeout(() => this.zone.run(() => this.showViewerFallback()), 30000);
  }

  onViewerLoaded(): void {
    clearTimeout(this.viewerWatchdog);
    if (!this.viewerOpen || !this.viewerSafeUrl) return;
    // The iframe 'load' fires once; with zone event coalescing the repaint would otherwise wait for
    // the next interaction, leaving the "Loading viewer" overlay stuck over the loaded page.
    this.zone.run(() => {
      this.viewerLoading = false;
      this.viewerFallback = false;
      this.cdr.detectChanges();
    });
  }

  private showViewerFallback(): void {
    clearTimeout(this.viewerWatchdog);
    this.zone.run(() => {
      this.viewerLoading = false;
      this.viewerFallback = true;
      this.cdr.detectChanges();
    });
  }

  closeViewer(): void {
    clearTimeout(this.viewerWatchdog);
    clearTimeout(this.viewerLoadingTimer);
    this.viewerOpen = false;
    this.viewerSafeUrl = null;
    this.viewerUrl = '';
    this.viewerLoading = false;
    this.viewerFallback = false;
    this.viewerError = '';
  }

  // Fallback escape hatches: a dedicated extension window (first-party auth) or a new tab.
  openInViewerWindow(): void {
    if (!this.viewerUrl) return;
    if (ext?.windows?.create) ext.windows.create({ url: this.viewerUrl, type: 'popup', width: 1100, height: 850 });
    else window.open(this.viewerUrl, '_blank');
    this.closeViewer();
  }

  openInViewerTab(): void {
    if (!this.viewerUrl) return;
    if (ext?.tabs?.create) ext.tabs.create({ url: this.viewerUrl });
    else window.open(this.viewerUrl, '_blank');
    this.closeViewer();
  }

  download(_doc: UcebDocument): void {
    this.menuFor = '';
    this.menuDoc = null;
    void this.runDownload(_doc);
  }

  private async runDownload(doc: UcebDocument): Promise<void> {
    const name = doc.name || doc.docId;
    this.showActionToast('loading', this.translate.instant('actions.downloading', { name }));
    try {
      const res = await this.docSvc.downloadDocument(doc.docId, doc.name);
      if (!res.saved && res.viewerUrl) {
        this.showActionToast('success', this.translate.instant('actions.openedInViewer', { name }));
        this.viewerOpen = true;
        this.viewerLoading = true;
        this.openViewer(res.viewerUrl, doc.name || doc.docId);
        return;
      }
      this.showActionToast('success', this.translate.instant('actions.downloaded', { name }));
    } catch (e: any) {
      this.showActionToast('error', e?.message || this.translate.instant('actions.downloadError'));
    }
  }

  // ---- delete (confirmation via MatDialog) ----
  @ViewChild('deleteDialog') private deleteDialog!: TemplateRef<unknown>;
  private deleteDialogRef?: MatDialogRef<unknown>;
  private snackRef?: MatSnackBarRef<TextOnlySnackBar>;
  deleteTarget: UcebDocument | null = null;
  deleting = false;
  deleteError = '';

  remove(doc: UcebDocument): void {
    this.deleteError = '';
    this.deleteTarget = doc;
    this.deleteDialogRef = this.dialog.open(this.deleteDialog, { panelClass: 'satori', autoFocus: false });
    this.deleteDialogRef.afterClosed().subscribe(() => {
      this.deleteTarget = null;
      this.deleteError = '';
    });
  }

  cancelDelete(): void {
    if (this.deleting) return;
    this.deleteDialogRef?.close();
  }

  async confirmDelete(): Promise<void> {
    const doc = this.deleteTarget;
    if (!doc || this.deleting) return;
    this.deleting = true;
    this.deleteError = '';
    try {
      await this.docSvc.deleteDocument(doc.docId);
      this.documents = this.documents.filter((d) => d.docId !== doc.docId);
      this.recompute();
      this.deleteDialogRef?.close();
      this.showActionToast('success', this.translate.instant('actions.deleted', { name: doc.name || doc.docId }));
    } catch (e: any) {
      this.deleteError = e?.message || this.translate.instant('actions.deleteError');
    } finally {
      this.deleting = false;
    }
  }

  dismissActionToast(): void {
    this.snackRef?.dismiss();
  }

  private showActionToast(kind: 'loading' | 'success' | 'error', text: string): void {
    this.snackRef?.dismiss();
    if (kind === 'loading') {
      this.snackRef = this.snackBar.open(text, undefined, { panelClass: 'satori' });
    } else {
      this.snackRef = this.snackBar.open(text, this.translate.instant('actions.dismiss'), {
        panelClass: 'satori',
        duration: 5000,
      });
    }
  }

  // ---- upload card ----

  async loadDocTypes(): Promise<void> {
    this.docTypesLoading = true;
    this.docTypesError = '';
    this.docTypesBoType = this.context?.businessObjectType || '';
    try {
      this.docTypes = await this.docSvc.listDocTypes(this.docTypesBoType);
      // With a record in context the list is the few CONFIGURED types, so warm each one's metadata
      // fields in the background — selecting a type (or IDP auto-selecting after classify) is then
      // instant instead of waiting on a per-type solution-config read.
      if (this.context?.businessObjectType) {
        for (const t of this.docTypes) {
          if (t) void this.ensureDocTypeFields(t).catch(() => {});
        }
      }
    } catch (e: any) {
      this.docTypesError = e?.message || 'Could not load document types.';
    } finally {
      this.docTypesLoading = false;
    }
  }

  // Cache key for a type's metadata fields. Includes the business object (and, for Workday, the record
  // id, since its default attributes are per-record) so a different record never reuses stale fields.
  private docTypeFieldsKey(docType: string): string {
    const workday = this.isWorkdayRecord();
    const boType = this.context?.businessObjectType || '';
    const boId = this.context?.businessObjectId || '';
    return `${workday ? 'w' : 'c'}|${boType}|${workday ? boId : ''}|${docType}`;
  }

  // Loads a type's metadata fields once, reusing any cached result or in-flight fetch.
  private ensureDocTypeFields(docType: string): Promise<DocTypeField[]> {
    const key = this.docTypeFieldsKey(docType);
    const cached = this.docTypeFieldsCache[key];
    if (cached) return Promise.resolve(cached);
    const inFlight = this.docTypeFieldsInFlight[key];
    if (inFlight) return inFlight;
    const p = this.docSvc
      .fetchDocTypeFields({
        docType,
        businessObjectType: this.context?.businessObjectType || '',
        businessObjectId: this.context?.businessObjectId || '',
        workday: this.isWorkdayRecord(),
      })
      .then((fields) => {
        this.docTypeFieldsCache[key] = fields;
        return fields;
      })
      .finally(() => {
        delete this.docTypeFieldsInFlight[key];
      });
    this.docTypeFieldsInFlight[key] = p;
    return p;
  }

  async loadIdpStatus(): Promise<void> {
    try {
      const status = await this.docSvc.fetchIdpStatus();
      this.idpConnected = status.connected;
      this.idpPublicHost = status.publicHost;
    } catch {
      this.idpConnected = false;
      this.idpPublicHost = false;
    }
  }

  get canAutoClassify(): boolean {
    return this.idpEnabled && !this.idpBusy && this.uploadFiles.length > 0 && this.idpConnected && this.idpPublicHost;
  }

  get anyClassifiedFiles(): boolean {
    return this.uploadFiles.some((f) => !!this.fileClassifications[f.name]);
  }

  async onDocTypeChange(): Promise<void> {
    const newType = this.selectedDocType.trim();
    const cls = this.selectedUploadName ? this.fileClassifications[this.selectedUploadName] : null;
    const selectedFile = this.selectedUploadName ? this.uploadFiles.find((f) => f.name === this.selectedUploadName) : null;
    const originalType = cls?.autoDocType || cls?.docType || null;

    if (this.selectedUploadName && originalType) {
      this.rememberUploadMeta(this.selectedUploadName, originalType, this.docTypeFieldValues);
    }

    if (cls && newType) {
      if (!('autoDocType' in cls)) cls.autoDocType = cls.docType || null;
      cls.docType = newType;
      cls.edited = newType !== (cls.autoDocType || null);
      cls.uploadable = true;
    }

    await this.loadUploadMetaFields();
    if (!cls || !newType) return;

    const file = selectedFile;
    if (!file || !this.docTypeFields.length) {
      this.uploadStatus = `${file ? file.name + ' → ' : ''}${newType}${cls.edited ? ' (edited)' : ''} · no metadata fields on this record.`;
      this.uploadError = false;
      return;
    }

    const saved = this.selectedUploadName ? this.uploadMetaSnapshots[this.selectedUploadName]?.[newType] : undefined;
    if (saved) {
      this.applyUploadMetaValues(saved);
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · restored saved metadata values.`;
      this.uploadError = false;
      return;
    }

    const classifiedValues = this.classifiedUploadMetaValues(cls?.fields);
    if (newType === originalType && Object.keys(classifiedValues).length) {
      this.rememberUploadMeta(file.name, newType, classifiedValues);
      this.applyUploadMetaValues(classifiedValues);
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · restored saved metadata values.`;
      this.uploadError = false;
      return;
    }

    try {
      this.uploadStatus = `Reading ${newType} metadata from ${file.name}…`;
      this.uploadError = false;
      const extracted = await this.docSvc.extractWithIdp(
        file,
        this.docTypeFields.map((field) => ({ id: field.id, name: field.label || field.id })),
      );
      cls.fields = extracted.map((field) => ({
        ...field,
        importable: this.docTypeFields.find((f) => f.id === field.id)?.importable !== false,
      }));
      let filled = 0;
      const nextValues: Record<string, string> = {};
      for (const field of extracted) {
        if (!field.value) continue;
        nextValues[field.id] = field.value;
        filled++;
      }
      this.rememberUploadMeta(file.name, newType, nextValues);
      // The user may have moved to another file/type while IDP was reading this one.
      if (this.selectedUploadName !== file.name || this.selectedDocType !== newType) return;
      this.applyUploadMetaValues(nextValues);
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · ${filled} field(s) filled — review, then Upload.`;
    } catch (e: any) {
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · metadata fill failed (${e?.message || 'unknown error'})`;
      this.uploadError = false;
    }
  }

  private isWorkdayContext(ctx: RecordContext | null): boolean {
    return ctx?.source === 'workday' || ctx?.businessObjectType === 'employee';
  }

  // A subordinate's Employee ID can render a beat late (prod Workday lazy-renders Job Details and the
  // detector runs an off-screen probe to hydrate it), so a real worker first looks "not authorised".
  // Re-check for ~12s; resolve true as soon as the page stops reporting notAuthorized.
  private async graceRecheckNotAuthorized(token: number): Promise<boolean> {
    for (let i = 0; i < 20; i++) {
      await new Promise((r) => setTimeout(r, 600));
      if (token !== this.loadSeq) return false;
      const ctx = await this.ctxSvc.getActiveContext();
      if (token !== this.loadSeq) return false;
      if (ctx && !ctx.notAuthorized) return true;
      // The auto-scroll finished and this is a confirmed peer/superior (Manager isn't the signed-in
      // user) - stop waiting and deny now instead of spinning out the full window.
      if (ctx && !ctx.probing && ctx.managerName && !this.isAuthorizedForWorker(ctx)) return false;
    }
    return false;
  }

  // Full-hierarchy gate: the signed-in user may view a worker only if it's themselves or one of their
  // reports (the worker's Manager == signed-in user). Workday exposes Employee IDs for superiors too,
  // so Employee-ID visibility is NOT a grant. Fails open if "self" is unknown (don't block everything).
  private isAuthorizedForWorker(ctx: RecordContext | null): boolean {
    const self = this.selfWorker;
    if (!self || !self.name) return true;
    // Workday descriptors carry a status suffix (e.g. "Anthony Rizzo1 (On Leave)") that the on-screen
    // name is stripped of, so drop "(...)" before comparing; also ignore digits ("Rizzo1" vs "Rizzo").
    const norm = (s?: string | null) =>
      (s || '').replace(/\([^)]*\)/g, ' ').toLowerCase().replace(/[^a-z0-9]+/g, '');
    const normNoDigits = (s?: string | null) => norm(s).replace(/\d+/g, '');
    const same = (a?: string | null, b?: string | null) =>
      !!norm(a) && !!norm(b) && (norm(a) === norm(b) || normNoDigits(a) === normNoDigits(b));
    // Your own profile is always allowed.
    if (ctx?.resolveQuery && self.employeeId && ctx.resolveQuery === self.employeeId) return true;
    if (same(ctx?.displayName, self.name)) return true;
    // Otherwise: only workers who report to you (their Manager is you).
    return same(ctx?.managerName, self.name);
  }

  // Resolves a Workday worker name / Employee ID to a WID (auto-apply on a unique match, else pick list).
  private async resolveAndLoadWorker(query: string, token: number): Promise<void> {
    this.resolving = true;
    this.resolveStatus = this.translate.instant('documents.worker.finding', { query });
    try {
      const result = await this.docSvc.resolveWorker(query);
      if (token !== this.loadSeq) return;
      if (result.total === 0) {
        this.resolveStatus = this.translate.instant('documents.worker.none', { query });
        return;
      }
      if (result.wid) {
        const match = result.matches.find((m) => m.wid === result.wid) || result.matches[0];
        await this.applyResolvedWorker(match);
        return;
      }
      this.resolveStatus = this.translate.instant('documents.worker.pick', { count: result.total, query });
      this.workerMatches = result.matches;
    } catch (e: any) {
      this.resolveStatus = this.translate.instant('documents.worker.failed', { detail: e?.message || 'unknown error' });
    } finally {
      this.resolving = false;
    }
  }

  // Applies a resolved worker as the active record and loads its documents. Used by the auto-match
  // path and by clicking a row in the pick list.
  async applyResolvedWorker(match: WorkerMatch): Promise<void> {
    if (!match.wid) return;
    this.context = {
      businessObjectType: 'employee',
      businessObjectId: match.wid,
      displayName: match.name || `employee ${match.wid}`,
      employeeId: match.employeeId || undefined,
      source: 'workday',
    };
    this.workerMatches = [];
    this.notAuthorized = false;
    this.resolveStatus = this.translate.instant('documents.worker.loadingDocs', { who: match.name || 'worker' });
    this.loading = true;
    this.error = '';
    try {
      const { documents, columns } = await this.docSvc.listForContext({
        businessObjectType: 'employee',
        businessObjectId: match.wid,
      });
      this.documents = documents;
      this.columns = columns;
      this.page = 1;
      this.recompute();
      void this.loadQueries();
    } catch (e: any) {
      this.error = e?.message || 'Could not load documents.';
    } finally {
      this.zone.run(() => {
        this.resolveStatus = '';
        this.loading = false;
        this.cdr.detectChanges();
      });
    }
  }

  // Record label shown in the context header. Workday workers show their name + Employee ID
  // (the opaque 32-hex WID is meaningless to a user); other systems show the plain record id.
  recordLabel(): string {
    const c = this.context;
    if (!c?.businessObjectId) return '';
    if (c.employeeId) {
      const name = c.displayName && c.displayName !== c.businessObjectId ? c.displayName.trim() : '';
      const idPart = this.translate.instant('documents.record.employeeId', { id: c.employeeId });
      return name ? `${name} \u00b7 ${idPart}` : idPart;
    }
    return this.translate.instant('documents.record.id', { id: c.businessObjectId });
  }

  // One-line summary for a worker match row (Employee ID / title / WID).
  workerMeta(m: WorkerMatch): string {
    const bits: string[] = [];
    if (m.employeeId) bits.push(`ID ${m.employeeId}`);
    if (m.businessTitle) bits.push(m.businessTitle);
    if (m.wid) bits.push(`WID ${m.wid}`);
    return bits.join(' \u00b7 ');
  }

  private isWorkdayRecord(): boolean {
    const type = (this.context?.businessObjectType || '').toLowerCase();
    const source = (this.context?.source || '').toLowerCase();
    return type === 'employee' || type === 'worker' || source.includes('workday');
  }

  private collectUploadMeta(): Array<{ name: string; value: string }> {
    const out: Array<{ name: string; value: string }> = [];
    for (const field of this.docTypeFields) {
      if (field.importable === false) continue;
      const value = (this.docTypeFieldValues[field.id] || '').trim();
      if (value) out.push({ name: field.id, value });
    }
    return out;
  }

  private classifiedUploadMetaValues(fields?: Array<{ id: string; value?: string }>): Record<string, string> {
    const values: Record<string, string> = {}
    ;
    if (!fields?.length) return values;

    for (const field of fields) {
      if (!field.value) continue;
      values[field.id] = field.value;
    }
    return values;
  }

  private applyUploadMetaValues(values: Record<string, string>): void {
    const next: Record<string, string> = {};
    for (const field of this.docTypeFields) {
      next[field.id] = values[field.id] || '';
    }
    this.docTypeFieldValues = next;
  }

  private rememberUploadMeta(fileName: string, docType: string, values: Record<string, string>): void {
    if (!fileName || !docType) return;
    this.uploadMetaSnapshots[fileName] ??= {};
    this.uploadMetaSnapshots[fileName][docType] = { ...values };
  }

  private async loadUploadMetaFields(): Promise<void> {
    this.docTypeFields = [];
    this.docTypeFieldsError = '';
    if (!this.selectedDocType) return;

    const docType = this.selectedDocType;
    const owner = this.selectedUploadName;
    // No spinner when the fields were already prefetched/cached — they render instantly.
    const cachedNow = this.docTypeFieldsCache[this.docTypeFieldsKey(docType)];
    this.docTypeFieldsLoading = !cachedNow;
    try {
      const fields = await this.ensureDocTypeFields(docType);
      // The user may have switched type/file while this was loading — ignore a stale result.
      if (this.selectedDocType !== docType || this.selectedUploadName !== owner) return;
      this.docTypeFields = fields;
      if (owner && this.fileClassifications[owner]) {
        const cls = this.fileClassifications[owner];
        const seedType = cls?.docType || this.selectedDocType;
        const saved = this.uploadMetaSnapshots[owner]?.[seedType];
        if (saved) {
          this.applyUploadMetaValues(saved);
          this.metaValuesFor = owner;
          return;
        }
      }
      // Keep typed values only when they belong to this same file (never another file's metadata).
      const carry = this.metaValuesFor === owner;
      const next: Record<string, string> = {};
      for (const field of this.docTypeFields) {
        next[field.id] = carry ? this.docTypeFieldValues[field.id] || '' : '';
      }
      this.docTypeFieldValues = next;
      this.metaValuesFor = owner;
    } catch (e: any) {
      this.docTypeFieldsError = e?.message || 'Could not load metadata fields.';
    } finally {
      this.docTypeFieldsLoading = false;
    }
  }

  onFilePick(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    this.addFiles(input.files);
    input.value = '';
  }

  onDrop(ev: DragEvent): void {
    ev.preventDefault();
    this.dragOver = false;
    this.addFiles(ev.dataTransfer?.files ?? null);
  }

  onDragOver(ev: DragEvent): void {
    ev.preventDefault();
    this.dragOver = true;
  }

  onDragLeave(): void {
    this.dragOver = false;
  }

  private addFiles(list: FileList | null): void {
    if (!list || !list.length) return;
    this.uploadFiles = this.uploadFiles.concat(Array.from(list));
    this.uploadStatus = '';
    this.uploadError = false;
  }

  removeFile(i: number): void {
    const removed = this.uploadFiles.splice(i, 1)[0];
    if (removed) {
      delete this.fileClassifications[removed.name];
      delete this.uploadMetaSnapshots[removed.name];
      if (this.selectedUploadName === removed.name) {
        this.selectedUploadName = null;
        this.docTypeFields = [];
        this.docTypeFieldValues = {};
        this.metaValuesFor = null;
      }
    }
  }

  async selectUploadFile(file: File): Promise<void> {
    this.selectedUploadName = file.name;
    const cls = this.fileClassifications[file.name];
    const docType = cls?.docType;
    const valid = docType && this.docTypes.includes(docType);
    this.selectedDocType = valid ? docType! : '';
    await this.loadUploadMetaFields();

    const snapshot = this.selectedUploadName && docType ? this.uploadMetaSnapshots[this.selectedUploadName]?.[docType] : undefined;
    const classifiedValues = this.classifiedUploadMetaValues(cls?.fields);
    let filled = 0;
    if (snapshot) {
      this.applyUploadMetaValues(snapshot);
      filled = Object.values(snapshot).filter((value) => !!value).length;
    } else if (Object.keys(classifiedValues).length) {
      this.rememberUploadMeta(file.name, docType || '', classifiedValues);
      this.applyUploadMetaValues(classifiedValues);
      filled = Object.values(classifiedValues).filter((value) => !!value).length;
    }

    const tag = cls?.edited ? ' (edited)' : '';
    if (!this.selectedDocType) {
      this.uploadStatus = `${file.name}: unknown type — pick a document type from the list to set it.`;
      this.uploadError = true;
    } else if (!this.docTypeFields.length) {
      this.uploadStatus = `${file.name} → ${this.selectedDocType}${tag} · this type has no metadata fields on this record.`;
      this.uploadError = false;
    } else {
      this.uploadStatus = `${file.name} → ${this.selectedDocType}${tag} · ${filled} field(s) filled — review, then Upload.`;
      this.uploadError = false;
    }
    // May run after fetch-based awaits (ensureDocTypeFields) that settle outside Angular's zone, and is
    // also called programmatically from autoClassify — repaint the selected file's fields/values now.
    this.zone.run(() => this.cdr.detectChanges());
  }

  async autoClassify(): Promise<void> {
    if (!this.uploadFiles.length) {
      this.uploadStatus = 'Add file(s) first, then Auto-classify.';
      this.uploadError = true;
      return;
    }
    if (!this.idpConnected || !this.idpPublicHost) {
      this.uploadStatus = 'IDP is not connected right now.';
      this.uploadError = true;
      return;
    }

    this.idpBusy = true;
    const boType = this.context?.businessObjectType || '';
    this.uploadStatus = `Classifying ${this.uploadFiles.length} file(s) with IDP…`;
    this.uploadError = false;
    try {
      const results = await this.docSvc.classifyWithIdp(this.uploadFiles, boType);
      for (const result of results) {
        this.fileClassifications[result.name] = {
          docType: result.docType || null,
          confidence: result.confidence || 0,
          reviewRequired: Boolean(result.reviewRequired),
          error: result.error,
          fields: Array.isArray(result.fields) ? result.fields : [],
          uploadable: result.uploadable !== false,
        };
      }

      const detected = results.filter((r) => r.docType).length;
      if (!detected) {
        this.uploadStatus = 'Could not detect a type — pick one manually.';
        this.uploadError = true;
        return;
      }

      const withFields = this.uploadFiles.find((file) => {
        const cls = this.fileClassifications[file.name];
        return cls?.docType && this.docTypes.includes(cls.docType) && (cls.fields || []).some((field) => field.value);
      });
      const firstClassified = this.uploadFiles.find((file) => this.fileClassifications[file.name]?.docType);
      const toSelect = withFields || firstClassified;
      if (toSelect) await this.selectUploadFile(toSelect);
    } catch (e: any) {
      this.uploadStatus = `Auto-classify failed: ${e?.message || 'unknown error'}`;
      this.uploadError = true;
    } finally {
      this.idpBusy = false;
      // classifyWithIdp resolves off Angular's zone (fetch), so repaint the classified state + cleared
      // spinner now instead of waiting for the next user click.
      this.zone.run(() => this.cdr.detectChanges());
    }
  }

  get canUpload(): boolean {
    return (
      !this.uploading &&
      !this.idpBusy &&
      this.uploadFiles.length > 0 &&
      (!!this.selectedDocType || this.uploadFiles.some((file) => !!this.fileClassifications[file.name]?.docType)) &&
      !!this.context?.businessObjectId
    );
  }

  get showUploadSpinner(): boolean {
    return this.uploading && !this.idpBusy;
  }

  get showAutoClassifySpinner(): boolean {
    return this.idpBusy;
  }

  private uploadTargetLabel(): string {
    const c = this.context;
    // Workday: show the human Employee ID (and name), never the opaque WID.
    if (c?.employeeId) {
      const name = c.displayName && c.displayName !== c.businessObjectId ? c.displayName.trim() : '';
      const idPart = this.translate.instant('documents.record.employeeId', { id: c.employeeId });
      return name ? `${name} · ${idPart}` : idPart;
    }
    const type = c?.businessObjectType || 'record';
    const prettyType = type ? `${type.charAt(0).toUpperCase()}${type.slice(1)}` : 'Record';
    return `${prettyType} ${c?.businessObjectId || ''}`.trim();
  }

  private metaForFile(file: File): Array<{ name: string; value: string }> {
    if (file.name === this.selectedUploadName) return this.collectUploadMeta();
    const cls = this.fileClassifications[file.name];
    if (cls && Array.isArray(cls.fields)) {
      return cls.fields
        .filter((field) => field.value && field.importable !== false)
        .map((field) => ({ name: field.id, value: field.value || '' }));
    }
    // No file picked in the list: the form's values apply to every file being uploaded.
    if (!this.selectedUploadName) return this.collectUploadMeta();
    return [];
  }

  async doUpload(): Promise<void> {
    if (!this.canUpload || !this.context) return;
    this.uploading = true;
    this.uploadStatus = '';
    this.uploadError = false;
    const countBefore = this.documents.length;
    try {
      const workday = this.isWorkdayRecord();
      const ctx = this.context!;
      const uploaded: string[] = [];
      const errors: string[] = [];
      const skipped: string[] = [];

      // Build one task per eligible file, then run them with limited concurrency so a batch uploads in
      // parallel (much faster than one-at-a-time) without flooding the capture broker. Flaky broker
      // 500s are retried in the BFF, so a little parallelism is safe.
      const tasks: Array<() => Promise<void>> = [];
      for (const file of this.uploadFiles) {
        const cls = this.fileClassifications[file.name];
        const fileDocType = cls?.docType || this.selectedDocType;
        if (!fileDocType) {
          errors.push(`${file.name}: no document type`);
          continue;
        }
        if (cls && cls.docType && cls.uploadable === false) {
          skipped.push(`${file.name} (${cls.docType})`);
          continue;
        }

        const meta = this.metaForFile(file);
        tasks.push(async () => {
          try {
            if (workday) {
              const result = await this.docSvc.capture({
                businessObjectType: ctx.businessObjectType || 'employee',
                businessObjectId: ctx.businessObjectId || '',
                documentTypeId: fileDocType,
                files: [file],
                additionalAttributes: meta,
              });
              uploaded.push(...result.captured);
              errors.push(...result.errors);
            } else {
              const result = await this.docSvc.upload({
                businessObjectType: ctx.businessObjectType || '',
                businessObjectId: ctx.businessObjectId || '',
                ecmContentTypeName: fileDocType,
                files: [file],
                additionalAttributes: meta,
              });
              uploaded.push(...result.uploaded);
              errors.push(...result.errors);
            }
          } catch (e: any) {
            errors.push(`${file.name}: ${e?.message || 'upload failed'}`);
          }
        });
      }

      const CONCURRENCY = 4;
      for (let i = 0; i < tasks.length; i += CONCURRENCY) {
        await Promise.all(tasks.slice(i, i + CONCURRENCY).map((t) => t()));
      }

      if (uploaded.length) {
        const target = this.uploadTargetLabel();
        const toastText = uploaded.length === 1
          ? this.translate.instant('actions.uploadedToRecord', { name: uploaded[0], target })
          : this.translate.instant('actions.uploadedManyToRecord', { count: uploaded.length, target });
        this.showActionToast('success', toastText);

        const parts: string[] = [];
        if (skipped.length) parts.push(`Skipped: ${skipped.join(', ')}`);
        if (errors.length) parts.push(`Failed: ${errors.join('; ')}`);
        this.uploadStatus = parts.join(' · ');
        this.uploadError = false;
        this.uploadFiles = [];
        this.fileClassifications = {};
        this.uploadMetaSnapshots = {};
        this.selectedUploadName = null;
        this.selectedDocType = '';
        this.docTypeFields = [];
        this.docTypeFieldValues = {};
        this.docTypeFieldsError = '';
        void this.refreshAfterUpload(countBefore + uploaded.length);
      } else {
        const parts: string[] = [];
        if (skipped.length) parts.push(`Skipped: ${skipped.join(', ')}`);
        if (errors.length) parts.push(`Failed: ${errors.join('; ')}`);
        this.uploadStatus = parts.join(' · ') || 'Upload failed.';
        this.uploadError = true;
      }
    } catch (e: any) {
      this.uploadStatus = e?.message || 'Upload failed.';
      this.uploadError = true;
    } finally {
      this.uploading = false;
    }
  }

  // CIC/OnBase search indexing lags the upload, so after the first reload keep polling quietly
  // (no skeleton) until the list holds the expected number of documents.
  private async refreshAfterUpload(expected: number): Promise<void> {
    await this.reload();
    const ctx = this.context;
    for (const delay of [2000, 3000, 5000, 8000]) {
      if (!ctx?.businessObjectId || !ctx.businessObjectType || this.documents.length >= expected) return;
      await new Promise((r) => setTimeout(r, delay));
      if (this.context !== ctx) return;
      const token = this.loadSeq;
      try {
        const { documents, columns } = await this.docSvc.listForContext({
          businessObjectType: ctx.businessObjectType,
          businessObjectId: ctx.businessObjectId,
        });
        if (token !== this.loadSeq || this.context !== ctx) return;
        if (documents.length > this.documents.length) {
          this.zone.run(() => {
            this.documents = documents;
            this.columns = columns;
            this.recompute();
            this.cdr.detectChanges();
          });
        }
      } catch {
        return;
      }
    }
  }
}
