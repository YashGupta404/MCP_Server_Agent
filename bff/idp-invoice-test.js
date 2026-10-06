// Isolates which Invoices field breaks the OnBase upload by uploading several field combinations.
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'it', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}
async function stage() {
  const b64 = Buffer.from('Invoice broker-error isolation test.\n').toString('base64');
  const res = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName: 'inv-iso.txt', mime: 'text/plain', dataBase64: b64 }) });
  return (await res.json()).stagingId;
}
async function tryUpload(label, attrs) {
  const stagingId = await stage();
  const r = await callTool('upload_staged_file', { stagingId, businessObjectId: RECORD, businessObjectType: 'account', ecmContentTypeName: 'Invoices', documentName: 'inv-iso', additionalAttributesJson: JSON.stringify(attrs) });
  const ok = !/error|failed|unexpected|500/i.test(r);
  console.log(`[${label}] -> ${ok ? 'OK' : 'FAIL'}: ${r.slice(0, 160)}`);
}

(async () => {
  await tryUpload('229 only (Invoice #)', [{ name: '229', value: 'INV-ISO-1' }]);
  await tryUpload('229+227 (+ Total)', [{ name: '229', value: 'INV-ISO-2' }, { name: '227', value: '$100.00' }]);
  await tryUpload('229+227+234 (+ Currency USD)', [{ name: '229', value: 'INV-ISO-3' }, { name: '227', value: '$100.00' }, { name: '234', value: 'USD' }]);
})();
