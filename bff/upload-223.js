// Uploads a test Invoices doc stamped with 228 (Vendor Name) + 223 (hfs_sObject_Id) = record id,
// then hits the default-list endpoint (list_documents) to see if it now appears.
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'up223', version: '0.1' } });
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
    // 1) stage a small file
    const b64 = Buffer.from('Test invoice stamped with hfs_sObject_Id (223) for default-list test.\n').toString('base64');
    const sres = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName: 'invoice-223-test.txt', mime: 'text/plain', dataBase64: b64 }) });
    const sjson = await sres.json();
    out.push('STAGE: ' + JSON.stringify(sjson));
    const stagingId = sjson.stagingId;

    // 2) upload with explicit 228 + 223 = record id
    const attrs = JSON.stringify([{ name: '228', value: RECORD }, { name: '223', value: RECORD }]);
    out.push('UPLOAD: ' + await callTool('upload_staged_file', {
      stagingId, businessObjectId: RECORD, businessObjectType: 'account',
      ecmContentTypeName: 'Invoices', documentName: 'invoice-223-test',
      additionalAttributesJson: attrs,
    }));

    // 3) default-list endpoint
    out.push('--- list_documents (default-list) ---');
    out.push(await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
  } catch (err) {
    out.push('ERROR: ' + err.message + '\n' + err.stack);
  }
  fs.writeFileSync(process.env.TEMP + '/up223.txt', out.join('\n'));
  console.log('done');
})();
