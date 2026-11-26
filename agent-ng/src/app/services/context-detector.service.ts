import { Injectable } from '@angular/core';
import { RecordContext } from '../types/record-context.model';

const ext: any = (globalThis as any).chrome ?? (globalThis as any).browser;

// Resolves the page's record context. Primary path: ask the ACTIVE tab's detector content script
// directly (and re-inject it if the extension was reloaded while the page stayed open). Fallback:
// the background's cached context (UCEB_GET_ACTIVE_CONTEXT, same message the old popup used).
@Injectable({ providedIn: 'root' })
export class ContextDetectorService {
  lastDebug = '';
  // Last tab URL seen (in-progress Workday feature; present so the documents view type-checks).
  lastTabUrl = '';

  // Re-run the detector probe (in-progress Workday feature). No-op stub for now.
  async reprobe(): Promise<void> {
    /* Workday auto-scroll probe — not yet implemented. */
  }

  async getActiveContext(): Promise<RecordContext | null> {
    const dbg: string[] = [`tabs=${!!ext?.tabs?.query}`, `rt=${!!ext?.runtime?.sendMessage}`, `scr=${!!ext?.scripting?.executeScript}`];
    let ctx = await this.fromActiveTab(dbg);
    if (!ctx) ctx = await this.fromBackground(dbg);
    this.lastDebug = dbg.join(' | ');
    return ctx;
  }

  // PoC: capture the active tab (ANY site) for AI context extraction — url + title + rendered text +
  // a cleaned DOM structure. Generic, not site-specific. A SINGLE fast injection into the top frame that
  // also walks SAME-ORIGIN child iframes (where SPAs/embedded apps like ServiceNow's gsft_main render the
  // record). pageHtml is HTML structure only (body; id/name/aria-*/data-*/role/href/labels kept for selectors):
  // scripts, styles, class/style attributes, svg, base64 and handlers are stripped so it
  // fits the model's context window. Cross-origin frames are unreadable and skipped.
  async captureActivePage(): Promise<{ pageUrl: string; pageTitle: string; pageContent: string; pageHtml: string } | null> {
    if (!ext?.tabs?.query) return null;
    let tab: any;
    try {
      [tab] = await ext.tabs.query({ active: true, currentWindow: true });
    } catch {
      return null;
    }
    if (!tab?.id) return null;
    if (ext?.scripting?.executeScript) {
      try {
        const [res] = await ext.scripting.executeScript({
          target: { tabId: tab.id },
          func: () => {
            const MAX_TEXT = 20000;
            const MAX_HTML = 120000;
            const textParts: string[] = [];
            const htmlParts: string[] = [];
            const seen = new Set<string>();
            const cleanHtml = (doc: Document): string => {
              try {
                const root = doc.body;
                if (!root) return '';
                const clone = root.cloneNode(true) as HTMLElement;
                clone.querySelectorAll('script,style,noscript,svg,canvas,link,meta,template,iframe,frame').forEach((n) => n.remove());
                try {
                  const w = doc.createTreeWalker(clone, NodeFilter.SHOW_COMMENT);
                  const cs: Node[] = [];
                  while (w.nextNode()) cs.push(w.currentNode);
                  cs.forEach((c) => c.parentNode?.removeChild(c));
                } catch {
                  /* ignore */
                }
                clone.querySelectorAll('*').forEach((el) => {
                  for (const a of Array.from(el.attributes)) {
                    const v = a.value || '';
                    if (a.name === 'style' || a.name === 'class' || /^on/i.test(a.name)) {
                      el.removeAttribute(a.name);
                      continue;
                    }
                    if (v.length > 300 || /^data:/i.test(v)) el.setAttribute(a.name, ''); // drop base64/huge values
                  }
                });
                return (clone.outerHTML || '').replace(/>\s+</g, '><').replace(/\s{2,}/g, ' ');
              } catch {
                return '';
              }
            };
            const grab = (doc: Document | null, depth: number) => {
              if (!doc || depth > 3) return;
              try {
                const t = (doc.body?.innerText || '').trim();
                if (t && !seen.has(t)) {
                  seen.add(t);
                  textParts.push(t);
                }
              } catch {
                /* ignore */
              }
              const h = cleanHtml(doc);
              if (h) htmlParts.push(h);
              // querySelectorAll doesn't cross shadow roots; ServiceNow's Next Experience puts the classic form
              // iframe (gsft_main) inside one, so collect iframes from every shadow root too.
              const roots: (Document | ShadowRoot)[] = [doc];
              try {
                for (let i = 0; i < roots.length && i < 500; i++) {
                  roots[i].querySelectorAll('*').forEach((el) => {
                    const sr = (el as HTMLElement).shadowRoot;
                    if (sr) roots.push(sr);
                  });
                }
              } catch {
                /* ignore */
              }
              let frames: HTMLIFrameElement[] = [];
              try {
                frames = roots.flatMap((r) => Array.from(r.querySelectorAll('iframe,frame')) as HTMLIFrameElement[]);
              } catch {
                frames = [];
              }
              for (const f of frames) {
                let cd: Document | null = null;
                try {
                  cd = f.contentDocument;
                } catch {
                  cd = null;
                }
                if (cd) grab(cd, depth + 1);
              }
            };
            grab(document, 0);
            return {
              url: location.href,
              title: document.title,
              text: textParts.join('\n\n---\n\n').slice(0, MAX_TEXT),
              html: htmlParts.join('\n<!-- frame -->\n').slice(0, MAX_HTML),
            };
          },
        });
        const r = res?.result;
        if (r) return { pageUrl: r.url || tab.url || '', pageTitle: r.title || tab.title || '', pageContent: r.text || '', pageHtml: r.html || '' };
      } catch {
        /* injection blocked (e.g. chrome:// or store pages) — fall through to url/title only */
      }
    }
    return { pageUrl: tab.url || '', pageTitle: tab.title || '', pageContent: '', pageHtml: '' };
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
