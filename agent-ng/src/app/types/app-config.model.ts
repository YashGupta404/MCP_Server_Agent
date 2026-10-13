// Runtime configuration loaded from /configs.json before the app bootstraps (see AppConfigService).
// Mirrors the OIFS pattern of a static Settings.config fed from a fetched config file.

export interface EntraTenant {
  /** Button label shown to the user, e.g. "onbase.onmicrosoft.com". */
  label: string;
  /** The Entra/Azure AD tenant domain used as the IAM domain_hint to federate straight to that org. */
  domain: string;
}

export interface AuthConfig {
  /** Nucleus IAM authorize endpoint (the hosted login page the user is redirected to). */
  authorizeEndpoint: string;
  /** Nucleus IAM token endpoint (used later by the callback to exchange the code). */
  tokenEndpoint: string;
  clientId: string;
  redirectUri: string;
  scope: string;
}

export interface AppConfig {
  /** Base URL of our BFF (all /api/* calls go here). */
  bffBaseUrl: string;
  idpEnabled: boolean;
  /** When true, demo services are used instead of the real BFF (OIFS-style demo swap). */
  demoMode: boolean;
  /** DEV ONLY: keep the sign-in session + chosen ECM across extension reloads so UI changes can be
   *  previewed without signing in and re-picking a system every time. Turn off before shipping. */
  devKeepSession?: boolean;
  /** Pre-fills the email on the sign-in screen and is passed to IAM as login_hint. */
  defaultLoginHint?: string;
  /** The "Sign in with Entra ID (…)" buttons. */
  entraTenants: EntraTenant[];
  auth: AuthConfig;
}
