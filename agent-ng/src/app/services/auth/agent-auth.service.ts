import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { Settings } from '../settings';

export interface LoginOptions {
  /** Pre-fills the email on the IAM page (OIDC login_hint). */
  loginHint?: string;
  /** Entra tenant domain -> federate straight to that org (OIDC domain_hint). */
  domainHint?: string;
  /** Persist the session across restarts. */
  remember?: boolean;
}

// Runs the interactive Nucleus IAM sign-in exactly like the existing extension: the extension performs
// the Authorization Code + PKCE step via chrome.identity.launchWebAuthFlow (redirect_uri = its own
// chromiumapp.org URL) to get a CODE, then hands the code + verifier to the BFF, which adds the client
// secret, exchanges for tokens, and returns an opaque session id. The password is only ever entered on
// the IAM-hosted page — never on localhost.
const ext: any = (globalThis as any).chrome ?? (globalThis as any).browser;
const SESSION_KEY = 'uceb_bff_session';

@Injectable({ providedIn: 'root' })
export class AgentAuthService {
  private readonly _signedIn$ = new BehaviorSubject<boolean>(false);
  readonly signedIn$ = this._signedIn$.asObservable();

  get isSignedIn(): boolean {
    return this._signedIn$.value;
  }

  private get sessionStore() {
    // chrome.storage.session is wiped on every extension reload, which forces a fresh sign-in after
    // each UI rebuild. devKeepSession parks it in storage.local so dev iteration keeps the session.
    if (Settings.config?.devKeepSession) return ext?.storage?.local ?? ext?.storage?.session;
    return ext?.storage?.session ?? ext?.storage?.local;
  }

  async getSession(): Promise<string | null> {
    const r = await this.sessionStore?.get(SESSION_KEY);
    return r?.[SESSION_KEY] ?? null;
  }

  async login(options: LoginOptions = {}): Promise<void> {
    if (!ext?.identity?.launchWebAuthFlow) {
      throw new Error('Sign-in is only available inside the extension.');
    }
    const auth = Settings.config?.auth;
    const bffBaseUrl = Settings.config?.bffBaseUrl;
    if (!auth?.authorizeEndpoint || !auth?.clientId || !bffBaseUrl) {
      throw new Error('Auth is not configured (configs.json).');
    }

    const redirectUri = ext.identity.getRedirectURL();
    const verifier = this.randomUrlToken();
    const challenge = await this.pkceChallenge(verifier);
    const state = this.randomUrlToken();

    const params = new URLSearchParams({
      response_type: 'code',
      client_id: auth.clientId,
      redirect_uri: redirectUri,
      scope: auth.scope,
      state,
      code_challenge: challenge,
      code_challenge_method: 'S256',
      prompt: 'login',
    });
    if (options.loginHint) params.set('login_hint', options.loginHint);
    // Entra button -> federate straight to that tenant; otherwise force the Hyland IAM login page.
    if (options.domainHint) params.set('domain_hint', options.domainHint);
    else params.set('acr_values', 'idp:local');

    const redirectResponse: string = await ext.identity.launchWebAuthFlow({
      url: `${auth.authorizeEndpoint}?${params.toString()}`,
      interactive: true,
    });

    const returned = new URL(redirectResponse);
    if (returned.searchParams.get('state') !== state) throw new Error('OAuth state mismatch — aborting.');
    const code = returned.searchParams.get('code');
    if (!code) throw new Error(`Authorization failed: ${returned.searchParams.get('error') ?? 'unknown_error'}`);

    const resp = await fetch(`${bffBaseUrl}/auth/exchange`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, codeVerifier: verifier, redirectUri }),
    });
    const data: any = await resp.json().catch(() => ({}));
    if (!resp.ok || !data.session) {
      throw new Error(`Token exchange failed: ${data?.detail || data?.error || 'HTTP ' + resp.status}`);
    }
    await this.sessionStore?.set({ [SESSION_KEY]: data.session });
    this._signedIn$.next(true);
  }

  // Drops the stored session (e.g. the BFF restarted and rejected it) so the app returns to sign-in.
  async clearSession(): Promise<void> {
    await this.sessionStore?.remove?.(SESSION_KEY);
    await ext?.storage?.local?.remove?.(SESSION_KEY);
    this._signedIn$.next(false);
  }

  private randomUrlToken(): string {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    return this.base64Url(bytes);
  }

  private async pkceChallenge(verifier: string): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return this.base64Url(new Uint8Array(digest));
  }

  private base64Url(bytes: Uint8Array): string {
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
}
