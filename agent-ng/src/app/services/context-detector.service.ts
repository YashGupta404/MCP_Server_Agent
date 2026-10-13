import { Injectable } from '@angular/core';
import { RecordContext } from '../types/record-context.model';

const ext: any = (globalThis as any).chrome ?? (globalThis as any).browser;

// Resolves the page's record context. Primary path: ask the ACTIVE tab's detector content script
// directly (and re-inject it if the extension was reloaded while the page stayed open). Fallback:
// the background's cached context (UCEB_GET_ACTIVE_CONTEXT, same message the old popup used).
@Injectable({ providedIn: 'root' })
export class ContextDetectorService {
  lastDebug = '';

  async getActiveContext(): Promise<RecordContext | null> {
    const dbg: string[] = [`tabs=${!!ext?.tabs?.query}`, `rt=${!!ext?.runtime?.sendMessage}`, `scr=${!!ext?.scripting?.executeScript}`];
    let ctx = await this.fromActiveTab(dbg);
    if (!ctx) ctx = await this.fromBackground(dbg);
    this.lastDebug = dbg.join(' | ');
    return ctx;
  }

  // Fires `cb` whenever the active tab/URL changes (navigation, tab switch) or a content script
  // re-publishes context. Returns an unsubscribe function.
  onChange(cb: () => void): () => void {
    const offs: Array<() => void> = [];
    if (ext?.tabs?.onActivated?.addListener) {
      const h = () => cb();
      ext.tabs.onActivated.addListener(h);
      offs.push(() => ext.tabs.onActivated.removeListener(h));
    }
    if (ext?.tabs?.onUpdated?.addListener) {
      const h = (_id: number, info: any, tab: any) => {
        if (tab?.active && (info?.url || info?.status === 'complete')) cb();
      };
      ext.tabs.onUpdated.addListener(h);
      offs.push(() => ext.tabs.onUpdated.removeListener(h));
    }
    if (ext?.runtime?.onMessage?.addListener) {
      const h = (msg: any) => {
        if (msg?.type === 'UCEB_CONTEXT') cb();
      };
      ext.runtime.onMessage.addListener(h);
      offs.push(() => ext.runtime.onMessage.removeListener(h));
    }
    return () => offs.forEach((o) => o());
  }

  private async fromActiveTab(dbg: string[]): Promise<RecordContext | null> {
    if (!ext?.tabs?.query) {
      dbg.push('noTabsApi');
      return null;
    }
    let tab: any;
    try {
      [tab] = await ext.tabs.query({ active: true, currentWindow: true });
    } catch (e: any) {
      dbg.push('queryErr:' + (e?.message || e));
      return null;
    }
    dbg.push(`tab=${tab?.id ?? '-'} url=${(tab?.url || '').replace(/^https?:\/\//, '').slice(0, 45)}`);
    if (!tab?.id) return null;

    const ask = async (): Promise<RecordContext | null> => {
      const res = await ext.tabs.sendMessage(tab.id, { type: 'UCEB_GET_CONTEXT' });
      return (res?.context ?? null) as RecordContext | null;
    };

    let ctx: RecordContext | null = null;
    try {
      ctx = await ask();
      dbg.push('ask=' + (ctx ? `${ctx.businessObjectType}:${ctx.businessObjectId ?? ctx.resolveQuery ?? '?'}` : 'null'));
    } catch (e: any) {
      dbg.push('askErr:' + (e?.message || e));
      try {
        await ext.scripting.executeScript({ target: { tabId: tab.id }, files: ['src/content/detector.js'] });
        ctx = await ask();
        dbg.push('reinject=' + (ctx ? 'ctx' : 'null'));
      } catch (e2: any) {
        dbg.push('reinjectErr:' + (e2?.message || e2));
      }
    }
    if (ctx) return ctx;

    // Fallback: parse the record straight from the tab URL (Salesforce / ServiceNow / Workday WID),
    // so we don't depend on the page's content script returning a context.
    const byUrl = this.parseUrl(tab.url || '');
    if (byUrl) dbg.push(`url=${byUrl.businessObjectType}:${byUrl.businessObjectId}`);
    return byUrl;
  }

  private parseUrl(url: string): RecordContext | null {
    let u: URL;
    try {
      u = new URL(url);
    } catch {
      return null;
    }
    const host = u.hostname;
    // Salesforce Lightning: /lightning/r/<Object>/<id>/view
    if (/\.(force|salesforce)\.com$/.test(host)) {
      const m = u.pathname.match(/\/lightning\/r\/([^/]+)\/([a-zA-Z0-9]{15,18})(?:\/|$)/);
      if (m) {
        return { businessObjectType: m[1].toLowerCase(), businessObjectId: m[2], displayName: `${m[1]} ${m[2]}`, source: host, url };
      }
    }
    // ServiceNow: /<table>.do?sys_id=<guid>
    if (/\.service-now\.com$/.test(host)) {
      const sysId = u.searchParams.get('sys_id');
      const doMatch = u.pathname.match(/\/([a-z_]+)\.do$/i);
      if (sysId && doMatch) {
        return { businessObjectType: doMatch[1].toLowerCase(), businessObjectId: sysId, displayName: sysId, source: host, url };
      }
    }
    // Workday: a bare 32-hex WID in the URL (employeeDocuments/<wid> etc.)
    if (/\.(workday|myworkday)\.com$/.test(host)) {
      const w = (u.pathname + u.hash).match(/([0-9a-fA-F]{32})/);
      if (w) {
        return { businessObjectType: 'employee', businessObjectId: w[1].toLowerCase(), displayName: w[1], source: host, url };
      }
    }
    return null;
  }

  private async fromBackground(dbg: string[]): Promise<RecordContext | null> {
    if (!ext?.runtime?.sendMessage) return null;
    try {
      const res = await ext.runtime.sendMessage({ type: 'UCEB_GET_ACTIVE_CONTEXT' });
      dbg.push('bg=' + (res?.context ? 'ctx' : 'null'));
      return res?.context ?? null;
    } catch (e: any) {
      dbg.push('bgErr:' + (e?.message || e));
      return null;
    }
  }
}
