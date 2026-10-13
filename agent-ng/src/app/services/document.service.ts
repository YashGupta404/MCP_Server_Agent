import { Injectable } from '@angular/core';
import { UcebDocument } from '../types/uceb-document.model';
import { BffApiService } from './bff/bff-api.service';

export interface QueryInfo {
  id: string;
  name?: string;
}
export interface QueryInput {
  id: string;
  name?: string;
  required?: boolean;
  minLength?: number;
  maxLength?: number;
  supportedOperators?: string[];
}
export interface QueryColumn {
  id?: string;
  name?: string;
}
export interface QueryMeta {
  inputs: QueryInput[];
  resultColumns: QueryColumn[];
}

export interface DocTypeField {
  id: string;
  label?: string;
  importable?: boolean;
}

export interface IdpStatus {
  connected: boolean;
  publicHost: boolean;
}

export interface IdpExtractedField {
  id: string;
  name?: string;
  value?: string;
  confidence?: number;
  reviewRequired?: boolean;
  importable?: boolean;
}

export interface IdpClassifyResult {
  name: string;
  docType: string | null;
  confidence: number;
  reviewRequired: boolean;
  error?: string;
  fields?: IdpExtractedField[];
  uploadable?: boolean;
}

// Lists the documents UCEB holds for a record (BFF /api/context -> MCP). Deterministic, no LLM.
@Injectable({ providedIn: 'root' })
export class DocumentService {
  constructor(private readonly bff: BffApiService) {}

  async listForContext(ctx: {
    businessObjectType: string;
    businessObjectId: string;
    onlyMine?: boolean;
  }): Promise<{ documents: UcebDocument[]; columns: string[] | null }> {
    const data = await this.bff.post<any>('/api/context', {
      businessObjectType: ctx.businessObjectType,
      businessObjectId: ctx.businessObjectId,
      onlyMine: !!ctx.onlyMine,
    });
    return {
      documents: Array.isArray(data.documents) ? data.documents : [],
      columns: Array.isArray(data.columns) && data.columns.length ? data.columns : null,
    };
  }

  // Lists the UCEB document (content) types available for the upload dropdown (BFF -> MCP).
  // Passing the business object lets the BFF answer from the solution config (fast) instead of
  // enumerating every content-type group in the repository.
  async listDocTypes(businessObjectType?: string): Promise<string[]> {
    const path = businessObjectType
      ? `/api/doctypes?businessObjectType=${encodeURIComponent(businessObjectType)}`
      : '/api/doctypes';
    const data = await this.bff.get<any>(path);
    return Array.isArray(data.types) ? data.types : [];
  }

  // Lists the user-editable metadata fields for a document type (BFF /api/doctype-fields),
  // so the upload form can render dynamic inputs like the old plugin did.
  async fetchDocTypeFields(ctx: {
    docType: string;
    businessObjectType?: string;
    businessObjectId?: string;
    workday?: boolean;
  }): Promise<DocTypeField[]> {
    const params = new URLSearchParams({ docType: ctx.docType || '' });
    if (ctx.businessObjectType) params.set('businessObjectType', ctx.businessObjectType);
    if (ctx.businessObjectId) params.set('businessObjectId', ctx.businessObjectId);
    if (ctx.workday) params.set('workday', 'true');
    const data = await this.bff.get<any>(`/api/doctype-fields?${params.toString()}`);
    return Array.isArray(data.fields) ? data.fields : [];
  }

  // Uploads files directly to the record's content (BFF /api/upload -> MCP staging -> upload_staged_file).
  async upload(ctx: {
    businessObjectType: string;
    businessObjectId: string;
    ecmContentTypeName: string;
    files: File[];
    additionalAttributes?: Array<{ name: string; value: string }>;
  }): Promise<{ uploaded: string[]; errors: string[] }> {
    const attachments = await Promise.all(ctx.files.map((f) => this.fileToAttachment(f)));
    const data = await this.bff.post<any>('/api/upload', {
      businessObjectType: ctx.businessObjectType,
      businessObjectId: ctx.businessObjectId,
      ecmContentTypeName: ctx.ecmContentTypeName,
      attachments,
      additionalAttributes: Array.isArray(ctx.additionalAttributes) ? ctx.additionalAttributes : [],
    });
    return {
      uploaded: Array.isArray(data.uploaded) ? data.uploaded : [],
      errors: Array.isArray(data.errors) ? data.errors : [],
    };
  }

  // Captures files via the Workday single-POST path (BFF /api/capture), matching the old plugin.
  async capture(ctx: {
    businessObjectType: string;
    businessObjectId: string;
    documentTypeId: string;
    files: File[];
    additionalAttributes?: Array<{ name: string; value: string }>;
  }): Promise<{ captured: string[]; errors: string[] }> {
    const attachments = await Promise.all(ctx.files.map((f) => this.fileToAttachment(f)));
    const data = await this.bff.post<any>('/api/capture', {
      businessObjectType: ctx.businessObjectType,
      businessObjectId: ctx.businessObjectId,
      documentTypeId: ctx.documentTypeId,
      businessObjectAttributes: [],
      additionalAttributes: Array.isArray(ctx.additionalAttributes) ? ctx.additionalAttributes : [],
      attachments,
    });
    return {
      captured: Array.isArray(data.captured) ? data.captured : [],
      errors: Array.isArray(data.errors) ? data.errors : [],
    };
  }

  // Whether IDP auto-classification is currently usable (valid cached token + public host configured).
  async fetchIdpStatus(): Promise<IdpStatus> {
    const data = await this.bff.get<any>('/api/idp/status').catch(() => ({} as any));
    return {
      connected: Boolean(data?.connected),
      publicHost: Boolean(data?.publicHost),
    };
  }

  // Detects each file's document type and returns the type/confidence plus pre-extracted field values.
  async classifyWithIdp(files: File[], businessObjectType?: string): Promise<IdpClassifyResult[]> {
    const attachments = await Promise.all((files ?? []).map((f) => this.fileToAttachment(f)));
    const data = await this.bff.post<any>('/api/idp/classify', {
      attachments,
      businessObjectType: businessObjectType || null,
    });
    return Array.isArray(data.results) ? data.results : [];
  }

  // Re-extracts metadata for one file after the user changes its document type.
  async extractWithIdp(file: File, fields: Array<{ id: string; name: string }>): Promise<IdpExtractedField[]> {
    const attachment = await this.fileToAttachment(file);
    const data = await this.bff.post<any>('/api/idp/extract', {
      attachment,
      fields: Array.isArray(fields) ? fields : [],
    });
    return Array.isArray(data.fields) ? data.fields : [];
  }

  // Resolves the in-panel viewer URL for a document (BFF /api/viewer -> MCP open_document_in_viewer).
  // The MCP builds the full CIC viewer URL (host + envKey + documents/cfs route) from the active system.
  async openInViewer(docId: string): Promise<string> {
    const data = await this.bff.post<any>('/api/viewer', { docId });
    if (!data?.url) throw new Error(data?.detail || data?.error || 'Viewer URL unavailable.');
    return data.url as string;
  }

  // Downloads the ORIGINAL document file (BFF /api/document/content -> MCP). Salesforce/CIC returns the
  // raw bytes (saved to disk); Workday may return the raw bytes via the HxP content platform, or a JSON
  // { workday, viewerUrl } envelope when no direct download exists — in which case we surface the viewer URL.
  async downloadDocument(docId: string, fileName?: string): Promise<{ saved: boolean; viewerUrl?: string }> {
    const resp = await this.bff.getRaw(`/api/document/content?docId=${encodeURIComponent(docId)}`);
    const contentType = resp.headers.get('content-type') || '';

    if (!resp.ok) {
      const detail = await resp.json().catch(() => ({} as any));
      throw new Error(detail?.detail || detail?.error || `Download failed (HTTP ${resp.status}).`);
    }

    // Workday-without-bytes path: the BFF forwards a JSON viewer-url envelope instead of a file.
    if (contentType.includes('application/json')) {
      const data = await resp.json().catch(() => ({} as any));
      return { saved: false, viewerUrl: data?.viewerUrl };
    }

    const blob = await resp.blob();
    const name = this.fileNameFromResponse(resp, fileName, docId);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    return { saved: true };
  }

  // Permanently deletes a document (BFF /api/document/delete -> MCP -> UCEB delete).
  async deleteDocument(docId: string): Promise<void> {
    const data = await this.bff.post<any>(`/api/document/delete?docId=${encodeURIComponent(docId)}`);
    if (!data?.deleted) throw new Error(data?.detail || data?.error || 'Delete failed.');
  }

  private fileNameFromResponse(resp: Response, fallbackName: string | undefined, docId: string): string {
    const disposition = resp.headers.get('content-disposition') || '';
    const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(disposition);
    const plain = /filename="?([^";]+)"?/i.exec(disposition);
    const fromHeader = star?.[1] ? decodeURIComponent(star[1]) : plain?.[1];
    return (fromHeader || fallbackName || docId).trim().replace(/^"|"$/g, '');
  }

  // ---- server-side query (keyword search), ported from agent.js ----

  // Lists the queries configured for a business object (BFF /api/queries -> solution config queryConfig).
  async fetchQueries(businessObjectType?: string): Promise<QueryInfo[]> {
    const path = businessObjectType
      ? `/api/queries?businessObjectType=${encodeURIComponent(businessObjectType)}`
      : '/api/queries';
    const data = await this.bff.get<any>(path);
    if (Array.isArray(data.queries)) return data.queries;
    try {
      const parsed = JSON.parse(data.raw ?? '{}');
      const queries = parsed?.data?.queries ?? parsed?.queries ?? [];
      return Array.isArray(queries) ? queries : [];
    } catch {
      return [];
    }
  }

  // A query's searchable inputs (with validation + operators) and result columns.
  async fetchQueryMetadata(queryId: string): Promise<QueryMeta> {
    const data = await this.bff.get<any>(`/api/query-metadata?queryId=${encodeURIComponent(queryId)}`);
    try {
      const d = JSON.parse(data.raw ?? '{}')?.data ?? {};
      return {
        inputs: Array.isArray(d.inputs) ? d.inputs : [],
        resultColumns: Array.isArray(d.resultColumns) ? d.resultColumns : [],
      };
    } catch {
      return { inputs: [], resultColumns: [] };
    }
  }

  // Runs a server-side query (keyword search) -> MCP query_documents.
  async executeQuery(req: {
    businessObjectType: string;
    queryId: string;
    businessObjectId?: string | null;
    filterFieldId?: string;
    filterValue?: string;
    filterOperator?: string;
  }): Promise<UcebDocument[]> {
    const data = await this.bff.post<any>('/api/query-execute', req);
    return Array.isArray(data.documents) ? data.documents : [];
  }

  private async fileToAttachment(file: File): Promise<{ name: string; mime: string; dataBase64: string }> {
    const buffer = await file.arrayBuffer();
    return {
      name: file.name,
      mime: file.type || 'application/octet-stream',
      dataBase64: this.bufferToBase64(buffer),
    };
  }

  private bufferToBase64(buffer: ArrayBuffer): string {
    const bytes = new Uint8Array(buffer);
    const chunk = 0x8000;
    let binary = '';
    for (let i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, Array.from(bytes.subarray(i, i + chunk)));
    }
    return btoa(binary);
  }
}
