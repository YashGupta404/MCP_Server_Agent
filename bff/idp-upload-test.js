// Verifies the listing fix: uploads a prescription tied to the account, then lists the account's docs.
const API = 'http://localhost:5200/mcp';
const STAGE = 'http://localhost:5200/staging/upload';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set.'); process.exit(1); }
const RECORD = '001gK00001KbuonQAB';

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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'ut', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

(async () => {
  const b64 = Buffer.from('Prescription test for listing fix. Medication: Amoxicillin.\n').toString('base64');
  const sres = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName: 'presc-listtest.txt', mime: 'text/plain', dataBase64: b64 }) });
  const stagingId = (await sres.json()).stagingId;
  const attrs = JSON.stringify([{ name: 'hfs_sObject_Id', value: RECORD }, { name: 'hfs_MedicationName', value: 'Amoxicillin 500 mg capsules' }, { name: 'hfs_PhysicianName', value: 'Dr. Sarah Lin, MD' }]);
  console.log('UPLOAD:', await callTool('upload_staged_file', { stagingId, businessObjectId: RECORD, businessObjectType: 'account', ecmContentTypeName: 'prescription', documentName: 'presc-listtest', additionalAttributesJson: attrs }));
  console.log('LIST:', await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
})();
