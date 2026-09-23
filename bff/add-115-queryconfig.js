// Add query 115 (Docs by sObjectId) to queryConfig[account] with a filter on hfs_sObject_Id (223),
// keep it as defaultListQuery, then test both the direct execute and the default-list endpoint.
const fs = require('fs');
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. In PowerShell run:  . .\\load-mcp-key.ps1  (loads it from dotnet user-secrets), then re-run.'); process.exit(1); }
const RECORD = '001gK00001KbuonQAB';
const QID = '115';

async function rpc(method, params, sessionId, protocol) {
  const headers = { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'X-Api-Key': KEY };
  if (sessionId) headers['Mcp-Session-Id'] = sessionId;
  if (protocol) headers['MCP-Protocol-Version'] = protocol;
  const isNotif = method.startsWith('notifications');
  const payload = { jsonrpc: '2.0', method, params };
  if (!isNotif) payload.id = Math.floor(Math.random() * 100000);
  const res = await fetch(API, { method: 'POST', headers, body: JSON.stringify(payload) });
  const sid = res.headers.get('mcp-session-id') || sessionId;
  const text = await res.text();
  let last = null;
  for (const line of text.split('\n')) { const t = line.trim(); if (t.startsWith('data:')) last = t.slice(5).trim(); }
  let json = null;
  try { json = last ? JSON.parse(last) : (text.trim() ? JSON.parse(text.trim()) : null); } catch (e) {}
  return { sid, json };
}
async function callTool(name, args) {
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'qc115', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

const ENTRY = {
  name: 'Docs by sObjectId', id: QID, label: null, description: null, type: 'PreConfigured', default: false,
  filterClauses: [{
    ecmFieldName: 'hfs_sObject_Id', ecmFieldId: '223', inputSource: '1',
    value: { value: 'Id', type: 'String', format: '' }, editable: false, operator: 'EqualsCaseInsensitive'
  }],
  displayColumns: { ecmColumnSets: [{ formFactorId: 'desktop', columns: ['228', '227', '229'] }], defaultFormFactor: 'desktop' },
  displayColumnConfig: [
    { ecmColumnName: 'Vendor Name', ecmColumnId: '228', type: 'String', localeStrings: null },
    { ecmColumnName: 'Invoice Total', ecmColumnId: '227', type: 'String', localeStrings: null },
    { ecmColumnName: 'Invoice #', ecmColumnId: '229', type: 'String', localeStrings: null }
  ]
};

(async () => {
  const out = [];
  try {
    const cfg = JSON.parse(await callTool('get_solution_configurations', {}));
    const boc = cfg.data.configurations.businessObjectConfig;
    const acct = boc.queryConfig.find(qc => qc.busObject === 'account');
    if (!acct.queries.find(q => q.id === QID)) acct.queries.push(ENTRY);
    out.push('account queries now: ' + acct.queries.map(q => q.id).join(','));
    out.push('SET: ' + await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) }));

    out.push('\n=== direct execute query 115 ===\n' + await callTool('query_documents', { businessObjectType: 'account', queryId: QID, filterFieldId: '223', filterValue: RECORD, filterOperator: 'EqualsCaseInsensitive' }));
    out.push('\n=== default-list endpoint ===\n' + await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
  } catch (err) {
    out.push('ERROR: ' + err.message);
  }
  fs.writeFileSync(process.env.TEMP + '/qc115.txt', out.join('\n'));
  console.log('done');
})();
