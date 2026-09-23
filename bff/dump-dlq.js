// Dumps the LIVE active-system additionalConfig: busObject / contentType / defaultListQuery id.
const fs = require('fs');
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'dlq', version: '0.1' } });
  const sid = init.sid; const proto = (init.json?.result?.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, proto);
  const r = await rpc('tools/call', { name, arguments: args }, sid, proto);
  const c = r.json?.result?.content;
  return Array.isArray(c) ? c.map(x => x.text).filter(Boolean).join('\n') : JSON.stringify(r.json);
}
(async () => {
  const out = [];
  try {
    const raw = await callTool('get_solution_configurations', {});
    const cfg = JSON.parse(raw);
    // find the config model — walk to businessObjectConfig.additionalConfig
    const dig = (o) => {
      if (!o || typeof o !== 'object') return null;
      if (Array.isArray(o.additionalConfig)) return o.additionalConfig;
      for (const k of Object.keys(o)) { const r = dig(o[k]); if (r) return r; }
      return null;
    };
    const ac = dig(cfg);
    if (!ac) { out.push('additionalConfig NOT FOUND. Top keys: ' + Object.keys(cfg).join(',')); }
    else {
      out.push('additionalConfig entries: ' + ac.length);
      for (const e of ac) {
        const dlq = e.defaultListQuery ? (e.defaultListQuery.id + ' (' + (e.defaultListQuery.name||'') + ')') : 'null';
        out.push(`${e.busObject} / ${e.ecmContentTypeName} -> defaultListQuery=${dlq}`);
      }
    }
  } catch (err) { out.push('ERR ' + err.message + '\n' + err.stack); }
  fs.writeFileSync(process.env.TEMP + '/dlq.txt', out.join('\n'));
  console.log('done');
})();
