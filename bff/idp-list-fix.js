// Fixes document LISTING for account->prescription so uploaded prescriptions appear in the panel:
//  1) adds hfs_sObject_Id (inputSource 1 = record Id) to the import mappings, so uploads tie the doc to the account.
//  2) adds a default "Prescriptions" list query filtered by hfs_sObject_Id = this record's Id.
// Reversible: full backup in %TEMP%/soln_backup.json. Run:  . .\load-mcp-key.ps1 ; node idp-list-fix.js
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. Run `. .\\load-mcp-key.ps1` first.'); process.exit(1); }
const BUS = 'account';
const TYPE = 'prescription';

async function rpc(method, params, sessionId, protocol) {
  const headers = { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'X-Api-Key': KEY };
  if (sessionId) headers['Mcp-Session-Id'] = sessionId;
  if (protocol) headers['MCP-Protocol-Version'] = protocol;
  const payload = { jsonrpc: '2.0', method, params };
  if (!method.startsWith('notifications')) payload.id = Math.floor(Math.random() * 100000);
  const res = await fetch(API, { method: 'POST', headers, body: JSON.stringify(payload) });
  const sid = res.headers.get('mcp-session-id') || sessionId;
  const text = await res.text();
  let last = null;
  for (const line of text.split('\n')) { const t = line.trim(); if (t.startsWith('data:')) last = t.slice(5).trim(); }
  let json = null; try { json = last ? JSON.parse(last) : (text.trim() ? JSON.parse(text.trim()) : null); } catch (e) {}
  return { sid, json };
}
async function callTool(name, args) {
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'lf', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

(async () => {
  const cfg = JSON.parse(await callTool('get_solution_configurations', {}));
  const boc = cfg.data.configurations.businessObjectConfig;

  // 1) ensure the record-linking field on account->prescription
  for (const e of boc.additionalConfig) {
    if (e.busObject === BUS && e.ecmContentTypeName === TYPE) {
      const mm = e.metadataFieldImportMappings || [];
      if (!mm.find(m => m.ecmFieldName === 'hfs_sObject_Id')) {
        mm.unshift({ ecmFieldName: 'hfs_sObject_Id', inputSource: '1', value: { value: 'Id', type: 'String', format: '' } });
      }
      e.metadataFieldImportMappings = mm;
    }
  }

  // 2) add a default Prescriptions list query (filter: hfs_sObject_Id == this record's Id)
  const presc = {
    name: 'Prescriptions', id: 'prescription', label: null, description: null, type: 'ContentType', default: true,
    filterClauses: [{
      ecmFieldName: 'hfs_sObject_Id', ecmFieldId: 'hfs_sObject_Id', inputSource: '1',
      value: { value: 'Id', type: 'String', format: '' }, editable: false, operator: 'Equals'
    }],
    displayColumns: { ecmColumnSets: [{ formFactorId: 'desktop', columns: ['docType', 'docId', 'hfs_MedicationName', 'hfs_PhysicianName', 'hfs_Pharmacy', 'hfs_Dosage'] }], defaultFormFactor: 'desktop' },
    displayColumnConfig: [
      { ecmColumnName: 'docType', ecmColumnId: 'docType', type: 'String', localeStrings: null },
      { ecmColumnName: 'docId', ecmColumnId: 'docId', type: 'String', localeStrings: null },
      { ecmColumnName: 'hfs_MedicationName', ecmColumnId: 'hfs_MedicationName', type: 'String', localeStrings: null },
      { ecmColumnName: 'hfs_PhysicianName', ecmColumnId: 'hfs_PhysicianName', type: 'String', localeStrings: null },
      { ecmColumnName: 'hfs_Pharmacy', ecmColumnId: 'hfs_Pharmacy', type: 'String', localeStrings: null },
      { ecmColumnName: 'hfs_Dosage', ecmColumnId: 'hfs_Dosage', type: 'String', localeStrings: null },
    ],
  };
  let acct = boc.queryConfig.find(q => q.busObject === BUS);
  if (!acct) { acct = { busObject: BUS, queries: [] }; boc.queryConfig.push(acct); }
  acct.queries.forEach(q => { q.default = false; });            // demote existing default
  acct.queries = acct.queries.filter(q => q.id !== 'prescription');
  acct.queries.unshift(presc);                                  // add ours as the default

  const res = await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) });
  console.log('SET RESULT:', res);
  const after = JSON.parse(await callTool('get_solution_configurations', {}));
  const aq = after.data.configurations.businessObjectConfig.queryConfig.find(q => q.busObject === BUS);
  console.log('account queries now:', JSON.stringify(aq.queries.map(q => ({ id: q.id, default: q.default }))));
})();
