// Central configuration for the extension.
// Fill the TODO values after (a) capturing the agent endpoint in DevTools and
// (b) registering a PUBLIC OAuth client in IAM. See yash-work/day-03.md.

export const CONFIG = {
  // ===== Backend-for-Frontend (BFF) — holds the OAuth secret + the user's IAM tokens =====
  // The extension talks ONLY to this backend. Login and agent calls are proxied through it,
  // so no client secret and no IAM token ever live in the browser.
  bff: {
    baseUrl: "https://localhost:5010",
  },

  // ===== CIC viewer, keyed by the active ECM system config (systemFriendlyName) =====
  // EVERY document — CIC-native or OnBase/CFS — renders in the CIC viewer inside the panel iframe.
  //   kind "documents": {host}/#/documents/{docId}?envKey={envKey}                 (CIC-native)
  //   kind "cfs":       {host}/#/cfs/{integrationId}/{docId}/{logicalPartId}?envKey=... (OnBase/CFS)
  // host + envKey are ENVIRONMENT-specific: Salesforce/CIC = dev, Workday/OnBase = staging.
  viewers: {
    // Salesforce / CIC-native — staging environment.
    cic: {
      kind: "documents",
      host: "https://cic-viewer.staging.app.hyland.com",
      envKey: "appintel-staging-prod",
    },
    // Workday / OnBase via CFS — staging environment.
    onbase_hcm_stg: {
      kind: "cfs",
      host: "https://cic-viewer.staging.app.hyland.com",
      envKey: "appintel-staging-prod",
      integrationId: "532c245c-a412-4f1e-8878-3097588179e5",
      logicalPartId: 1,
    },
    // Salesforce / OnBase via CFS — staging environment.
    // NOTE: integrationId is a candidate (CF_Salesforce_OnBase from the CFS Admin Portal) — verify it.
    OnBase9714: {
      kind: "cfs",
      host: "https://cic-viewer.staging.app.hyland.com",
      envKey: "appintel-staging-prod",
      integrationId: "14f19da9-705e-4674-b1a4-7d97617515bd",
      logicalPartId: 1,
    },
  },
  // Fallback when the active system config isn't listed above.
  defaultViewer: {
    kind: "documents",
    host: "https://cic-viewer.staging.app.hyland.com",
    envKey: "appintel-staging-prod",
  },

  // ===== Hyland Agent Builder (dev / appintel-dev-test) =====
  agent: {
    id: "a4374edc-32b0-4d01-bc45-8dbc496ed9c6",

    // Base host for the Agent Orchestrator API. TODO(confirm): verify via one DevTools capture
    // that the Studio chat hits this host at /v1 (it may proxy through /bff — if so, point
    // apiBaseUrl at that base and adjust the path in agent.js).
    apiBaseUrl: "https://appintel-dev-test.agent-studio.ai.dev.app.hyland.com",

    // Synchronous invoke: POST {apiBaseUrl}/v1/agents/{id}/versions/{versionId}/invoke
    // `latest` = most recent published version of the agent.
    versionId: "latest",

    // The invoke endpoint returns the full response in one JSON payload (no polling).
    streaming: false,
  },

  // ===== OAuth (Authorization Code + PKCE, PUBLIC client — NO secret) =====
  // DEV IAM — the agent lives in the dev environment, so the client must be a DEV client.
  auth: {
    authorizeEndpoint: "https://auth.dev.app.hyland.com/idp/connect/authorize",
    tokenEndpoint: "https://auth.dev.app.hyland.com/idp/connect/token",
    endSessionEndpoint: "https://auth.dev.app.hyland.com/idp/connect/endsession",

    // Public/PKCE client "yash-plugin-to-ai-client" in DEV IAM (Application: Agent Builder).
    // Redirect URI (confirmed via getRedirectURL()):
    //   https://hmeanojcjlkalipmknanlcdimhhfjneb.chromiumapp.org/
    clientId: "wsc-5906caef-219c-4277-a4d8-2d86fb2fc1a0",

    // Agent Orchestration API requires `hxp environment_authorization` (per docs
    // AgentBuilderPlatform/UserGuide/Authentication). openid/profile/offline_access added for the
    // user login + silent refresh. NOTE: docs only document client_credentials (service user +
    // secret) for programmatic access — PKCE is not a documented method; confirm the invoke API
    // accepts a user bearer token with these scopes.
    scopes: "openid profile offline_access hxp environment_authorization",
  },
};
