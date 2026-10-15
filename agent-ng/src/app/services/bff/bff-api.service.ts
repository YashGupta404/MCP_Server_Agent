import { Injectable, NgZone } from '@angular/core';
import { AgentAuthService } from '../auth/agent-auth.service';
import { Settings } from '../settings';

// Thin HTTP client to the BFF. Attaches the opaque BFF session id (X-BFF-Session) to every call and
// maps error payloads to thrown Errors. Replaces the per-call fetch boilerplate from the old agent.js.
@Injectable({ providedIn: 'root' })
export class BffApiService {
  constructor(private readonly auth: AgentAuthService, private readonly zone: NgZone) {}

  getProfile(): Promise<{ name: string; email: string; givenName: string; familyName: string; initials: string }> {
    return this.get('/api/profile');
  }

  private get base(): string {
    return Settings.config?.bffBaseUrl ?? '';
  }

  get<T>(path: string): Promise<T> {
    return this.inZone(this.request<T>('GET', path));
  }

  post<T>(path: string, body?: unknown): Promise<T> {
    return this.inZone(this.request<T>('POST', path, body));
  }

  // Raw fetch (keeps the Response) for binary/alternate content types such as a document download,
  // where the body may be a file blob (Salesforce) or a JSON viewer-url envelope (Workday).
  async getRaw(path: string): Promise<Response> {
    return this.inZone(this.rawFetch(path));
  }

  // fetch/chrome.storage continuations resolve outside Angular's change-detection zone, which would
  // strand every state update that follows a BFF call (no re-render). Re-settle the promise inside
  // the zone so a change-detection tick fires once the data lands — fixes it for every caller.
  private inZone<T>(p: Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      p.then(
        (v) => this.zone.run(() => resolve(v)),
        (e) => this.zone.run(() => reject(e)),
      );
    });
  }

  private async rawFetch(path: string): Promise<Response> {
    const session = await this.auth.getSession();
    if (!session) throw new Error('Not signed in.');

    const resp = await fetch(`${this.base}${path}`, {
      method: 'GET',
      headers: { 'X-BFF-Session': session },
    });
    if (resp.status === 401) {
      await this.auth.clearSession();
      throw new Error('Your session expired — please sign in again.');
    }
    return resp;
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const session = await this.auth.getSession();
    if (!session) throw new Error('Not signed in.');

    const headers: Record<string, string> = { 'X-BFF-Session': session };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const resp = await fetch(`${this.base}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const data: any = await resp.json().catch(() => ({}));
    if (!resp.ok) {
      // A stale session (e.g. the BFF restarted) must send the user back to sign-in rather than
      // surfacing a confusing error on every panel call.
      if (resp.status === 401) {
        await this.auth.clearSession();
        throw new Error('Your session expired — please sign in again.');
      }
      throw new Error(data?.detail || data?.error || `HTTP ${resp.status}`);
    }
    return data as T;
  }
}
