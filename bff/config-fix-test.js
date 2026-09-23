// 1) Add hfs_sObject_Id (223) to Invoices metadataFieldImportMappings so uploads can stamp it.
// 2) Upload a test invoice with 228 + 223 = record id.
// 3) Hit the default-list endpoint with defaultListQuery=102, then =109, to see if the doc appears.
const fs = require('fs');
const API = 'http://localhost:5200/mcp';
const STAGE = 'http://localhost:5200/staging/upload';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. In PowerShell run:  . .\\load-mcp-key.ps1  (loads it from dotnet user-secrets), then re-run.'); process.exit(1); }
const RECORD = '001gK00001KbuonQAB';

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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'fix', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}
async function setDLQ(id) {
  const cfg = JSON.parse(await callTool('get_solution_configurations', {}));
  const boc = cfg.data.configurations.businessObjectConfig;
  for (const e of boc.additionalConfig) {
    if (e.busObject === 'account' && e.ecmContentTypeName === 'Invoices') {
      const mm = e.metadataFieldImportMappings || [];
      if (!mm.find(m => m.ecmFieldName === '223')) mm.push({ ecmFieldName: '223', inputSource: '1', value: { value: 'Id', type: 'String', format: '' } });
      e.metadataFieldImportMappings = mm;
      e.defaultListQuery = { name: id === '109' ? 'Invoices' : 'Get Accounts', id, type: 'PreConfigured', default: false };
    }
  }
  return await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) });
}

(async () => {
  const out = [];
  try {
    out.push('add-223 + dLQ=102: ' + await setDLQ('102'));

    const b64 = Buffer.from('Invoice with hfs_sObject_Id 223 for default-list test.\n').toString('base64');
    const sres = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName: 'invoice-223b.txt', mime: 'text/plain', dataBase64: b64 }) });
    const stagingId = (await sres.json()).stagingId;
    const attrs = JSON.stringify([{ name: '228', value: RECORD }, { name: '223', value: RECORD }]);
    out.push('UPLOAD: ' + await callTool('upload_staged_file', { stagingId, businessObjectId: RECORD, businessObjectType: 'account', ecmContentTypeName: 'Invoices', documentName: 'invoice-223b', additionalAttributesJson: attrs }));

    out.push('\n--- default-list with dLQ=102 ---\n' + await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));

    out.push('\nswitch dLQ=109: ' + await setDLQ('109'));
    out.push('\n--- default-list with dLQ=109 ---\n' + await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
  } catch (err) {
    out.push('ERROR: ' + err.message + '\n' + err.stack);
  }
  fs.writeFileSync(process.env.TEMP + '/fixtest.txt', out.join('\n'));
  console.log('done');
})();
