// Clean verify for OnBase Invoices: upload with Invoice # (229) + Invoice Total (227), then list the
// account's docs (Invoices has a "Docs by sObjectId" list query, so it should appear).
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'vi', version: '0.1' } });
  const sid = init.sid;
  const protocol = (init.json && init.json.result && init.json.result.protocolVersion) || '2025-06-18';
  await rpc('notifications/initialized', {}, sid, protocol);
  const r = await rpc('tools/call', { name, arguments: args }, sid, protocol);
  const content = r.json && r.json.result && r.json.result.content;
  if (Array.isArray(content)) return content.map(c => c.text).filter(Boolean).join('\n');
  return JSON.stringify(r.json);
}

(async () => {
  const b64 = Buffer.from('Invoice verify. Invoice # INV-VERIFY-1 Total $4,815.00\n').toString('base64');
  const sres = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName: 'inv-verify.txt', mime: 'text/plain', dataBase64: b64 }) });
  const stagingId = (await sres.json()).stagingId;
  const attrs = JSON.stringify([{ name: '229', value: 'INV-VERIFY-1' }, { name: '227', value: '$4,815.00' }]);
  console.log('UPLOAD:', await callTool('upload_staged_file', { stagingId, businessObjectId: RECORD, businessObjectType: 'account', ecmContentTypeName: 'Invoices', documentName: 'inv-verify', additionalAttributesJson: attrs }));
  console.log('LIST:', await callTool('list_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
})();
