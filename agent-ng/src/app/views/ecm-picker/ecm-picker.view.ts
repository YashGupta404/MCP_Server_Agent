import { ChangeDetectorRef, Component, NgZone, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { SatTagCategory } from '@hylandsoftware/satori-ui/tag';
import { BffApiService } from '../../services/bff/bff-api.service';
import { ContextDetectorService } from '../../services/context-detector.service';
import { SystemConfigService } from '../../services/system-config.service';
import { SystemConfig } from '../../types/system-config.model';

interface CaptureProposal {
  businessObjectType: string;
  businessObjectId: string;
  docTypeId: string;
  docTypeName: string;
  recordFieldId: string;
  recordFieldName: string;
  confidence: number | null;
  confidenceDetail: string;
  fields: { id: string; name: string; value: string }[];
}

@Component({
  selector: 'agent-ecm-picker',
  templateUrl: './ecm-picker.view.html',
  styleUrls: ['./ecm-picker.view.scss'],
  standalone: false,
})
export class EcmPickerView implements OnInit {
  profileInitials = '--';
  loading = true;
  error = '';
  configs: SystemConfig[] = [];
  active = '';
  selecting = '';
  connected = '';

  readonly skeletons = [1, 2, 3, 4, 5];

  // ---- AI context detection (PoC: context_extractor Agent Builder agent, any website) ----
  aiBusy = false;
  aiError = '';
  aiContextText = '';
  // Last businessContext from the context agent; reused by Auto-map to avoid a second agent call.
  private aiContext: any = null;
  private aiContextUrl = '';

  // ---- KE auto-config (PoC): sample docs -> KE classify/NER -> generated solution-config mapping ----
  mapBusy = false;
  mapError = '';
  mapResultText = '';
  mapFiles: File[] = [];

  // ---- Zero-setup capture: review the agent's proposal, then write the config entry + upload ----
  proposal: CaptureProposal | null = null;
  captureBusy = false;
  captureError = '';
  captureDone = '';
  captureResultText = '';
  private mapAttachments: { name: string; mime: string; dataBase64: string }[] = [];

  constructor(
    private readonly bff: BffApiService,
    private readonly systems: SystemConfigService,
    private readonly router: Router,
    private readonly translate: TranslateService,
    private readonly zone: NgZone,
    private readonly cdr: ChangeDetectorRef,
    private readonly ctxSvc: ContextDetectorService,
  ) {}

  // PoC: capture the active tab on ANY site and ask the context_extractor agent (via BFF) what the
  // business object is. Needs only sign-in — no MCP.
  async detectAiContext(): Promise<void> {
    this.aiBusy = true;
    this.aiError = '';
    this.aiContextText = '';
    try {
      const page = await this.ctxSvc.captureActivePage();
      if (!page) {
        this.aiError = 'Could not read the active tab.';
        return;
      }
      const data = await this.bff.post<any>('/api/context/extract', page);
      const result = data?.context ?? data?.raw ?? data;
      this.aiContext = data?.context ?? null;
      this.aiContextUrl = page.pageUrl;
      // Un-escape \n so multi-line fields (e.g. basis) render line-by-line in the <pre>.
      this.aiContextText = JSON.stringify(result, null, 2).replace(/\\n/g, '\n');
    } catch (e: any) {
      this.aiError = e?.message || 'Failed to detect context.';
    } finally {
      this.aiBusy = false;
      this.cdr.detectChanges();
    }
  }

  // PoC: the admin drops sample documents. The BFF runs KE Context on them, fetches the live ECM catalog, and
  // the solution_config_mapper_poc agent proposes every mapping; the BFF validates ids against the catalog.
  onMapFiles(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    this.mapFiles = input.files ? Array.from(input.files) : [];
  }

  async autoMap(): Promise<void> {
    if (!this.mapFiles.length) {
      this.mapError = 'Select at least one sample document.';
      return;
    }
    this.mapBusy = true;
    this.mapError = '';
    this.mapResultText = '';
    this.proposal = null;
    this.captureError = '';
    this.captureDone = '';
    this.captureResultText = '';
    try {
      const page = await this.ctxSvc.captureActivePage();
      if (!page) {
        this.mapError = 'Could not read the active tab.';
        return;
      }
      // The side panel survives tab switches; only reuse the context detected on this same page.
      let businessContext = this.aiContextUrl === page.pageUrl ? this.aiContext : null;
      if (!businessContext) {
        const ctxData = await this.bff.post<any>('/api/context/extract', page);
        businessContext = ctxData?.context ?? null;
        this.aiContext = businessContext;
        this.aiContextUrl = page.pageUrl;
      }
      const attachments = await Promise.all(
        this.mapFiles.map(async (f) => ({
          name: f.name,
          mime: f.type || 'application/octet-stream',
          dataBase64: await this.fileToBase64(f),
        })),
      );
      const data = await this.bff.post<any>('/api/autoconfig/agent-map', {
        attachments,
        businessContext,
        pageContent: page.pageContent,
      });
      this.mapAttachments = attachments;
      this.proposal = this.toProposal(data?.mappings, businessContext, data?.captureConfidence);
      this.mapResultText = JSON.stringify(data, null, 2).replace(/\\n/g, '\n');
    } catch (e: any) {
      this.mapError = e?.message || 'Failed to generate mapping.';
    } finally {
      this.mapBusy = false;
      this.cdr.detectChanges();
    }
  }

  // Turns the validated agent mapping into an editable capture proposal.
  private toProposal(m: any, ctx: any, cc: any): CaptureProposal | null {
    const docTypeId = m?.doctypeMapping?.ecmDocumentTypeId;
    if (!m || !docTypeId) return null;
    const recordFieldId = m.recordLinkageMapping?.boContext?.ecmAttributeId || '';
    const fields = (Array.isArray(m.fieldImportMappings) ? m.fieldImportMappings : [])
      .filter((f: any) => f?.ecmAttributeId && f.ecmAttributeId !== recordFieldId)
      .map((f: any) => ({
        id: String(f.ecmAttributeId),
        name: f.ecmAttributeName || String(f.ecmAttributeId),
        value: f.sampleValue ?? '',
      }));
    return {
      businessObjectType: String(ctx?.businessObjectType || m.doctypeMapping?.busObject || '').toLowerCase(),
      businessObjectId: String(ctx?.businessObjectId || ''),
      docTypeId: String(docTypeId),
      docTypeName: m.doctypeMapping?.ecmDocumentTypeName || String(docTypeId),
      recordFieldId: String(recordFieldId),
      recordFieldName: m.recordLinkageMapping?.boContext?.ecmAttributeName || String(recordFieldId),
      confidence: typeof cc?.overall === 'number' ? cc.overall : null,
      confidenceDetail: cc
        ? `doctype ${cc.doctype?.score} (${cc.doctype?.reason}) · keywords ${cc.keywords?.populated}/${cc.keywords?.total} filled` +
          (cc.keywords?.missing?.length ? ` (missing: ${cc.keywords.missing.join(', ')})` : '')
        : '',
      fields,
    };
  }

  // Writes the capture-only config entry for (business object, doctype), then uploads with the reviewed values.
  async uploadMapped(): Promise<void> {
    const p = this.proposal;
    if (!p || !this.mapAttachments.length) return;
    if (!p.businessObjectId) {
      this.captureError = 'No record was detected on this page.';
      return;
    }
    this.captureBusy = true;
    this.captureError = '';
    this.captureDone = '';
    this.captureResultText = '';
    try {
      const data = await this.bff.post<any>('/api/capture/commit', {
        attachments: [this.mapAttachments[0]],
        businessObjectType: p.businessObjectType,
        businessObjectId: p.businessObjectId,
        documentTypeId: p.docTypeId,
        recordFieldId: p.recordFieldId,
        fields: p.fields.map((f) => ({ id: f.id, value: f.value })),
      });
      this.captureDone = `Captured as document ${data?.documentId} (${data?.documentType?.name}, group ${data?.documentType?.groupId}).`;
      if (data?.skippedFields?.length) this.captureDone += ` Skipped: ${data.skippedFields.join('; ')}.`;
      this.captureDone += data?.configRestored ? ' Solution config restored.' : ' Warning: solution config was NOT restored.';
      this.captureResultText = JSON.stringify(data, null, 2).replace(/\\n/g, '\n');
    } catch (e: any) {
      this.captureError = e?.message || 'Upload failed.';
    } finally {
      this.captureBusy = false;
      this.cdr.detectChanges();
    }
  }

  private fileToBase64(f: File): Promise<string> {
    return new Promise((resolve, reject) => {
      const r = new FileReader();
      r.onload = () => resolve(String(r.result).split(',')[1] ?? '');
      r.onerror = () => reject(new Error('Could not read file.'));
      r.readAsDataURL(f);
    });
  }

  async ngOnInit(): Promise<void> {
    void this.loadProfile();
    try {
      const { configs, active } = await this.systems.list();
      // A freshly provisioned environment has no registered system config yet, so the registry comes
      // back empty. CIC is always resolvable by default, so fall back to a single CIC entry instead of
      // stranding the user on an empty picker.
      this.configs = configs.length ? configs : [{ friendlyName: 'CIC', systemType: 'CIC', isDefault: true }];
      this.active = active;
    } catch (e: any) {
      this.error = e?.message || this.translate.instant('ecm.loadError');
    } finally {
      // chrome.storage/fetch continuations can land outside Angular's zone — force a tick so the
      // loaded list actually renders instead of staying on the skeletons.
      this.zone.run(() => {
        this.loading = false;
        this.cdr.detectChanges();
      });
    }
  }

  private async loadProfile(): Promise<void> {
    try {
      const profile = await this.bff.getProfile();
      this.profileInitials = profile.initials || '--';
      this.cdr.detectChanges();
    } catch {
      /* non-fatal */
    }
  }

  async choose(c: SystemConfig): Promise<void> {
    if (this.selecting) return;
    this.selecting = c.friendlyName;
    this.error = '';
    try {
      this.active = await this.systems.setActive(c.friendlyName);
      this.connected = this.active;
      this.router.navigate(['/documents']);
    } catch (e: any) {
      this.error = e?.message || this.translate.instant('ecm.connectError');
    } finally {
      this.selecting = '';
    }
  }

  badgeCategory(systemType: string): SatTagCategory {
    const t = (systemType || '').toLowerCase();
    if (t.includes('onbase')) return 'blue';
    if (t.includes('cic')) return 'green';
    if (t.includes('nuxeo')) return 'teal';
    return 'gray';
  }

  close(): void {
    window.close();
  }
}
