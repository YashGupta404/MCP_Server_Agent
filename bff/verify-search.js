// Verifies COM - Application docs are searchable by Entity Name (232). Reliable Node MCP client.
const fs = require('fs');
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. In PowerShell run:  . .\\load-mcp-key.ps1  (loads it from dotnet user-secrets), then re-run.'); process.exit(1); }

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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'vs', version: '0.1' } });
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
    out.push('=== 110 Contains "Beta" ===\n' + await callTool('query_documents', { businessObjectType: 'account', queryId: '110', filterFieldId: '232', filterValue: 'Beta', filterOperator: 'ContainsCaseInsensitive' }));
    out.push('=== 110 Contains "Acme" ===\n' + await callTool('query_documents', { businessObjectType: 'account', queryId: '110', filterFieldId: '232', filterValue: 'Acme', filterOperator: 'ContainsCaseInsensitive' }));
    out.push('=== 110 Contains "Industries" ===\n' + await callTool('query_documents', { businessObjectType: 'account', queryId: '110', filterFieldId: '232', filterValue: 'Industries', filterOperator: 'ContainsCaseInsensitive' }));
  } catch (err) { out.push('ERROR: ' + err.message); }
  fs.writeFileSync(process.env.TEMP + '/vsearch.txt', out.join('\n\n'));
  console.log('done');
})();
