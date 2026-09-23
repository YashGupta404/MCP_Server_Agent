// Wire query 115 (Docs by sObjectId) as account/Invoices defaultListQuery and test the default-list.
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'q115', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

(async () => {
  const out = [];
  try {
    out.push('=== get_query_metadata(115) ===\n' + await callTool('get_query_metadata', { queryId: QID }));

    const cfg = JSON.parse(await callTool('get_solution_configurations', {}));
    for (const e of cfg.data.configurations.businessObjectConfig.additionalConfig) {
      if (e.busObject === 'account' && e.ecmContentTypeName === 'Invoices') {
        e.defaultListQuery = { name: 'Docs by sObjectId', id: QID, type: 'PreConfigured', default: false };
      }
    }
    out.push('\nSET dLQ=115: ' + await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) }));

    out.push('\n=== list_documents (default-list) ===\n' + await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
  } catch (err) {
    out.push('ERROR: ' + err.message);
  }
  fs.writeFileSync(process.env.TEMP + '/q115.txt', out.join('\n'));
  console.log('done');
})();
