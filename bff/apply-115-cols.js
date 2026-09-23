// Aligns queryConfig[account] query 115 display columns to the GENERIC system columns that OnBase
// query 115 actually returns now (Document Name / Document Date / Document Type), so the default
// list shows populated columns for EVERY doc type (not just Invoices keywords).
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
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cols', version: '0.1' } });
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
    const boc = cfg?.data?.configurations?.businessObjectConfig ?? cfg?.configurations?.businessObjectConfig;
    const qc = (boc?.queryConfig || []).find(q => q.busObject === 'account');
    const q115 = (qc?.queries || []).find(q => q.id === '115');
    if (!q115) throw new Error('query 115 not found in account queryConfig');

    q115.displayColumns = {
      ecmColumnSets: [{ formFactorId: 'desktop', columns: ['DocumentName', 'DocumentDate', 'DocumentTypeName'] }],
      defaultFormFactor: 'desktop',
    };
    q115.displayColumnConfig = [
      { ecmColumnName: 'Document Name', ecmColumnId: 'DocumentName', type: 'String', localeStrings: null },
      { ecmColumnName: 'Document Date', ecmColumnId: 'DocumentDate', type: 'String', localeStrings: null },
      { ecmColumnName: 'Document Type', ecmColumnId: 'DocumentTypeName', type: 'String', localeStrings: null },
    ];

    out.push('SET: ' + await callTool('set_solution_configuration', { dataJson: JSON.stringify(cfg.data) }));

    // verify
    out.push('\n=== query_documents(115) after ===');
    out.push(await callTool('query_documents', { businessObjectType: 'account', queryId: '115', filterFieldId: '223', filterValue: '001gK00001KbuonQAB', filterOperator: 'EqualsCaseInsensitive', maxResults: 50 }));
  } catch (err) { out.push('ERR ' + err.message + '\n' + err.stack); }
  fs.writeFileSync(process.env.TEMP + '/cols115.txt', out.join('\n'));
  console.log('done');
})();
