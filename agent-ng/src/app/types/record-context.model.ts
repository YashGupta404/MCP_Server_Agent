// The record detected on the current browser page (from the detector.js content script, via the
// background service worker). Salesforce/ServiceNow give businessObjectId directly; Workday worker
// pages come through as needsResolve/notAuthorized (handled later).
export interface RecordContext {
  businessObjectType?: string;
  businessObjectId?: string;
  displayName?: string;
  source?: string;
  url?: string;
  needsResolve?: boolean;
  resolveQuery?: string;
  notAuthorized?: boolean;
}
