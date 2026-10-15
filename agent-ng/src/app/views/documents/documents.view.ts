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
import { DocTypeField, DocumentService, IdpClassifyResult, QueryInfo, QueryInput } from '../../services/document.service';
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
  docTypesLoading = false;
  docTypesError = '';
  selectedDocType = '';
  docTypeFields: DocTypeField[] = [];
  docTypeFieldsLoading = false;
  docTypeFieldsError = '';
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
    try {
      const { active } = await this.systems.list();
      this.activeSystem = active;
    } catch {
      /* non-fatal: header just won't show the system name */
    }
    await this.reload();
    // React to navigation / tab switches so the record + doclist stay contextual.
    this.unsub = this.ctxSvc.onChange(() => this.zone.run(() => this.scheduleContextCheck()));
    this.loadDocTypes();
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
    // Mid-navigation the URL/content script can momentarily resolve to nothing — don't wipe the
    // current list on a transient null; only react once a real record is on the tab.
    if (!ctx?.businessObjectId) return;
    const key = (c: RecordContext | null) => `${c?.businessObjectType || ''}:${c?.businessObjectId || ''}`;
    const changed = key(ctx) !== key(this.context);
    const emptyRetry = !this.loading && !this.error && this.documents.length === 0;
    if (changed || emptyRetry) {
      await this.reload();
    }
  }

  async reload(): Promise<void> {
    this.loading = true;
    this.error = '';
    this.menuFor = '';
    this.menuDoc = null;
    try {
      const ctx = await this.resolveContext();
      // Never wipe an already-loaded list when the page momentarily reports no record
      // (Salesforce fires many navigation events; a transient null must not blank the table).
      if (!ctx?.businessObjectId || !ctx?.businessObjectType) {
        if (!this.context?.businessObjectId) {
          this.documents = [];
          this.columns = null;
          this.recompute();
        }
        return;
      }
      this.context = ctx;
      const { documents, columns } = await this.docSvc.listForContext({
        businessObjectType: ctx.businessObjectType,
        businessObjectId: ctx.businessObjectId,
      });
      this.documents = documents;
      this.columns = columns;
      this.page = 1;
      this.recompute();
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

  // The content script / background may still be warming up when the panel opens; retry briefly.
  private async resolveContext(): Promise<RecordContext | null> {
    for (let i = 0; i < 4; i++) {
      const ctx = await this.ctxSvc.getActiveContext();
      if (ctx?.businessObjectId) return ctx;
      await new Promise((r) => setTimeout(r, 300));
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

  async toggleQuery(): Promise<void> {
    this.queryOpen = !this.queryOpen;
    if (this.queryOpen) {
      if (!this.queriesLoaded) await this.loadQueries();
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

  private async loadQueries(): Promise<void> {
    this.queriesLoading = true;
    this.queryStatus = '';
    this.queryStatusError = false;
    try {
      this.queries = await this.docSvc.fetchQueries(this.context?.businessObjectType || '');
      this.queriesLoaded = true;
    } catch (e: any) {
      this.queryStatus = e?.message || 'Could not load queries.';
      this.queryStatusError = true;
    } finally {
      this.queriesLoading = false;
    }
  }

  async onQueryChange(): Promise<void> {
    this.queryInputs = [];
    this.queryValues = {};
    this.queryOps = {};
    this.queryStatus = '';
    this.queryStatusError = false;
    const qid = this.selectedQueryId;
    if (!qid) return;
    this.queryFieldsLoading = true;
    try {
      let cached = this.queryMetaCache[qid];
      if (!cached) {
        const meta = await this.docSvc.fetchQueryMetadata(qid);
        cached = {
          inputs: meta.inputs || [],
          cols: (meta.resultColumns || []).map((c) => c.name || '').filter(Boolean),
        };
        this.queryMetaCache[qid] = cached;
      }
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
  chatTurns: Array<{ role: 'user' | 'agent' | 'error'; text: string; html?: SafeHtml; files?: string[] }> = [];
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
      const reply = await this.chat.send(this.buildOutgoing(text), attachments, this.context);
      this.chatTurns.push({
        role: 'agent',
        text: reply,
        html: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(reply)),
      });
      this.openViewerFromReply(reply);
      if (this.context?.businessObjectId && files.length) await this.reload();
    } catch (e: any) {
      this.chatTurns.push({ role: 'error', text: 'Error: ' + (e?.message || 'Agent call failed.') });
    } finally {
      this.chatSending = false;
      this.scrollChat();
    }
  }

  // Append the on-screen record as a hint so "this record" resolves without the user typing an id.
  private buildOutgoing(text: string): string {
    const c = this.context;
    if (!c?.businessObjectId) return text;
    const hint =
      `\n\n[Context \u2014 the user is viewing this record in the browser: ` +
      `businessObjectType=${c.businessObjectType}, businessObjectId=${c.businessObjectId}. ` +
      `Use these when listing or uploading documents for "this record".]`;
    return (text + hint).trim();
  }

  // If the agent's reply contains a Hyland viewer link, also open it in the in-panel viewer.
  private openViewerFromReply(reply: string): void {
    if (typeof reply !== 'string') return;
    const urls = reply.match(/https?:\/\/[^\s)"'\]]+/gi) || [];
    const viewer = urls
      .map((u) => u.replace(/[.,);\]]+$/, ''))
      .find((u) => /cic-viewer|\/#\/(?:documents|cfs)\//i.test(u));
    if (!viewer) return;
    const idMatch = reply.match(/document\s+([A-Za-z0-9_-]+)/i);
    this.openViewer(viewer, idMatch ? `Document ${idMatch[1]}` : 'Document');
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
    try {
      this.docTypes = await this.docSvc.listDocTypes(this.context?.businessObjectType || '');
    } catch (e: any) {
      this.docTypesError = e?.message || 'Could not load document types.';
    } finally {
      this.docTypesLoading = false;
    }
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
    if (saved && Object.keys(saved).length) {
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
      this.applyUploadMetaValues(nextValues);
      if (this.selectedUploadName) {
        this.rememberUploadMeta(this.selectedUploadName, newType, nextValues);
      }
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · ${filled} field(s) filled — review, then Upload.`;
    } catch (e: any) {
      this.uploadStatus = `${file.name} → ${newType}${cls.edited ? ' (edited)' : ''} · metadata fill failed (${e?.message || 'unknown error'})`;
      this.uploadError = false;
    }
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

    this.docTypeFieldsLoading = true;
    try {
      const workday = this.isWorkdayRecord();
      this.docTypeFields = await this.docSvc.fetchDocTypeFields({
        docType: this.selectedDocType,
        businessObjectType: this.context?.businessObjectType || '',
        businessObjectId: this.context?.businessObjectId || '',
        workday,
      });
      if (this.selectedUploadName && this.fileClassifications[this.selectedUploadName]) {
        const cls = this.fileClassifications[this.selectedUploadName];
        const seedType = cls?.docType || this.selectedDocType;
        const saved = this.uploadMetaSnapshots[this.selectedUploadName]?.[seedType];
        if (saved && Object.keys(saved).length) {
          this.applyUploadMetaValues(saved);
          return;
        }
      }
      const next: Record<string, string> = {};
      for (const field of this.docTypeFields) {
        next[field.id] = this.docTypeFieldValues[field.id] || '';
      }
      this.docTypeFieldValues = next;
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
    if (snapshot && Object.keys(snapshot).length) {
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
    this.uploading = true;
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
      this.uploading = false;
    }
  }

  get canUpload(): boolean {
    return (
      !this.uploading &&
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
    const type = this.context?.businessObjectType || 'record';
    const prettyType = type ? `${type.charAt(0).toUpperCase()}${type.slice(1)}` : 'Record';
    return `${prettyType} ${this.context?.businessObjectId || ''}`.trim();
  }

  private metaForFile(file: File): Array<{ name: string; value: string }> {
    if (file.name === this.selectedUploadName) return this.collectUploadMeta();
    const cls = this.fileClassifications[file.name];
    if (cls && Array.isArray(cls.fields)) {
      return cls.fields
        .filter((field) => field.value && field.importable !== false)
        .map((field) => ({ name: field.id, value: field.value || '' }));
    }
    return [];
  }

  async doUpload(): Promise<void> {
    if (!this.canUpload || !this.context) return;
    this.uploading = true;
    this.uploadStatus = '';
    this.uploadError = false;
    try {
      const workday = this.isWorkdayRecord();
      const uploaded: string[] = [];
      const errors: string[] = [];
      const skipped: string[] = [];

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
        try {
          if (workday) {
            const result = await this.docSvc.capture({
              businessObjectType: this.context.businessObjectType || 'employee',
              businessObjectId: this.context.businessObjectId || '',
              documentTypeId: fileDocType,
              files: [file],
              additionalAttributes: meta,
            });
            uploaded.push(...result.captured);
            errors.push(...result.errors);
          } else {
            const result = await this.docSvc.upload({
              businessObjectType: this.context.businessObjectType || '',
              businessObjectId: this.context.businessObjectId || '',
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
        await this.reload();
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
}
