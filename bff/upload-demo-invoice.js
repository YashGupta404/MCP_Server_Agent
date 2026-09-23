// Uploads clean demo Invoices with REAL metadata (Vendor Name, Invoice Total, Invoice #, Invoice Date)
// plus hfs_sObject_Id (223) = record id for default-list scoping, then lists the record's docs.
const fs = require('fs');
const API = 'http://localhost:5200/mcp';
const STAGE = 'http://localhost:5200/staging/upload';
const KEY = process.env.MCP_API_KEY;
if (!KEY) { console.error('MCP_API_KEY not set. In PowerShell run:  . .\\load-mcp-key.ps1  (loads it from dotnet user-secrets), then re-run.'); process.exit(1); }
const RECORD = '001gK00001KbuonQAB';

// field ids: 223 hfs_sObject_Id | 228 Vendor Name | 227 Invoice Total | 229 Invoice # | 226 Invoice Date
const DOCS = [
  { vendor: 'Acme Supplies', total: '4200', invNo: 'INV-1007', date: '2026-09-18' },
  { vendor: 'Globex Corp', total: '18750', invNo: 'INV-2043', date: '2026-09-15' },
];

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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'updemo', version: '0.1' } });
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
    for (const d of DOCS) {
      const body = `INVOICE\nVendor: ${d.vendor}\nInvoice #: ${d.invNo}\nTotal: ${d.total}\nDate: ${d.date}\n`;
      const b64 = Buffer.from(body).toString('base64');
      const fileName = `${d.invNo}.txt`;
      const sres = await fetch(STAGE, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY }, body: JSON.stringify({ fileName, mime: 'text/plain', dataBase64: b64 }) });
      const sjson = await sres.json();
      out.push(`STAGE ${fileName}: ` + JSON.stringify(sjson));
      const attrs = JSON.stringify([
        { name: '223', value: RECORD },
        { name: '228', value: d.vendor },
        { name: '227', value: d.total },
        { name: '229', value: d.invNo },
        { name: '226', value: d.date },
      ]);
      out.push(`UPLOAD ${d.invNo}: ` + await callTool('upload_staged_file', {
        stagingId: sjson.stagingId, businessObjectId: RECORD, businessObjectType: 'account',
        ecmContentTypeName: 'Invoices', documentName: d.invNo, additionalAttributesJson: attrs,
      }));
    }
    out.push('--- record listing ---');
    out.push(await callTool('query_documents', { businessObjectType: 'account', businessObjectId: RECORD }));
  } catch (err) {
    out.push('ERROR: ' + err.message + '\n' + err.stack);
  }
  fs.writeFileSync(process.env.TEMP + '/updemo.txt', out.join('\n'));
  console.log('done');
})();
