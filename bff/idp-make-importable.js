// Makes the prescription metadata fields CAPTURABLE on account->prescription uploads by adding them to
// metadataFieldImportMappings with inputSource "6" (caller-provided). Reversible: backup in %TEMP%/soln_backup.json.
// Run after:  . .\load-mcp-key.ps1   (so MCP_API_KEY is set), then:  node idp-make-importable.js
const API = 'http://localhost:5200/mcp';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. Run `. .\\load-mcp-key.ps1` first.'); process.exit(1); }

const BUS = 'account';
const TYPE = 'prescription';
// Only STRING fields are made caller-provided (inputSource 6) — UCEB stores them as-is from IDP's text.
// Typed fields (Integer/Decimal/DateTime) are intentionally NOT importable: UCEB requires real typed values
// and IDP returns strings, so they stay preview-only until the upload path coerces types.
const FIELDS = [
  { ecmFieldName: 'hfs_MedicationName', type: 'String' },
  { ecmFieldName: 'hfs_Dosage', type: 'String' },
  { ecmFieldName: 'hfs_MultiSpeciality', type: 'String' },
  { ecmFieldName: 'hfs_Pharmacy', type: 'String' },
  { ecmFieldName: 'hfs_PhysicianName', type: 'String' },
  { ecmFieldName: 'hfs_test', type: 'String' },
];

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
  let json = null;
  try { json = last ? JSON.parse(last) : (text.trim() ? JSON.parse(text.trim()) : null); } catch (e) {}
  return { sid, json };
}
async function callTool(name, args) {
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'imp', version: '0.1' } });
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
  let touched = false;
  for (const e of boc.additionalConfig) {
    if (e.busObject === BUS && e.ecmContentTypeName === TYPE) {
      // Keep any non-caller-provided mappings (record/static/etc.), then set OUR string fields as inputSource 6.
      const kept = (e.metadataFieldImportMappings || []).filter(
        (m) => m.inputSource !== '6' && !FIELDS.some((f) => f.ecmFieldName === m.ecmFieldName)
      );
      const ours = FIELDS.map((f) => ({ ecmFieldName: f.ecmFieldName, inputSource: '6', value: { value: '', type: f.type, format: '' } }));
      e.metadataFieldImportMappings = [...kept, ...ours];
      touched = true;
      console.log(`Updated ${BUS} -> ${TYPE}: ${e.metadataFieldImportMappings.length} import mapping(s) (${ours.length} caller-provided).`);
    }
  }
  if (!touched) { console.error(`No ${BUS} -> ${TYPE} entry found!`); process.exit(1); }
  const res = await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) });
  console.log('SET RESULT:', res);
  // verify
  const after = JSON.parse(await callTool('get_solution_configurations', {}));
  const e2 = after.data.configurations.businessObjectConfig.additionalConfig.find(e => e.busObject === BUS && e.ecmContentTypeName === TYPE);
  console.log('VERIFY import mappings:', JSON.stringify(e2.metadataFieldImportMappings.map(m => ({ f: m.ecmFieldName, src: m.inputSource })), null, 0));
})();
