// Fetches the ACTIVE system's solution config (onbase_hcm_stg / Workday) and writes it pretty-printed
// to the workspace root as workday-solution-config.json so it can be read directly.
const fs = require('fs');
const path = require('path');
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. In PowerShell run:  . .\\load-mcp-key.ps1  (loads it from dotnet user-secrets), then re-run.'); process.exit(1); }
async function rpc(method, params, sid, proto) {
  const headers = { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'X-Api-Key': KEY };
  if (sid) headers['Mcp-Session-Id'] = sid;
  if (proto) headers['MCP-Protocol-Version'] = proto;
  const payload = { jsonrpc: '2.0', method, params };
  if (!method.startsWith('notifications')) payload.id = Math.floor(Math.random() * 100000);
  const res = await fetch(API, { method: 'POST', headers, body: JSON.stringify(payload) });
  const nsid = res.headers.get('mcp-session-id') || sid;
  const text = await res.text();
  let last = null;
  for (const l of text.split('\n')) { const t = l.trim(); if (t.startsWith('data:')) last = t.slice(5).trim(); }
  let json = null; try { json = last ? JSON.parse(last) : JSON.parse(text.trim()); } catch (e) {}
  return { sid: nsid, json };
}
async function callTool(name, args) {
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'wdcfg', version: '0.1' } });
  const sid = init.sid; const proto = (init.json?.result?.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, proto);
  const r = await rpc('tools/call', { name, arguments: args }, sid, proto);
  const c = r.json?.result?.content;
  return Array.isArray(c) ? c.map(x => x.text).filter(Boolean).join('\n') : JSON.stringify(r.json);
}
(async () => {
  try {
    const raw = await callTool('get_solution_configurations', {});
    const parsed = JSON.parse(raw);
    const out = path.join(__dirname, '..', 'workday-solution-config.json');
    fs.writeFileSync(out, JSON.stringify(parsed, null, 2));
    console.log('WROTE ' + out + ' (' + JSON.stringify(parsed).length + ' bytes)');
  } catch (err) {
    console.log('ERR ' + err.message + '\n' + err.stack);
  }
})();
