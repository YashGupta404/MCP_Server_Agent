// Restores the solution config exactly from the pre-change backup (%TEMP%/soln_backup.json), then
// re-applies ONLY the proven change (6 String prescription fields importable). Run: . .\load-mcp-key.ps1 ; node idp-restore.js
const fs = require('fs');
const path = require('path');
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set.'); process.exit(1); }

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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'rs', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

(async () => {
  let raw = fs.readFileSync(path.join(process.env.TEMP, 'soln_backup.json'), 'utf8');
  raw = raw.slice(raw.indexOf('{'));
  const backup = JSON.parse(raw);
  console.log('RESTORE:', await callTool('set_solution_configuration', { dataJson: JSON.stringify(backup.data) }));

  // Re-apply the proven change: 6 String prescription fields as inputSource 6 (importable).
  const cfg = JSON.parse(await callTool('get_solution_configurations', {}));
  const boc = cfg.data.configurations.businessObjectConfig;
  const FIELDS = ['hfs_MedicationName', 'hfs_Dosage', 'hfs_MultiSpeciality', 'hfs_Pharmacy', 'hfs_PhysicianName', 'hfs_test'];
  for (const e of boc.additionalConfig) {
    if (e.busObject === 'account' && e.ecmContentTypeName === 'prescription') {
      const kept = (e.metadataFieldImportMappings || []).filter(m => m.inputSource !== '6' && !FIELDS.includes(m.ecmFieldName));
      const ours = FIELDS.map(f => ({ ecmFieldName: f, inputSource: '6', value: { value: '', type: 'String', format: '' } }));
      e.metadataFieldImportMappings = [...kept, ...ours];
    }
  }
  console.log('RE-APPLY:', await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) }));
})();
