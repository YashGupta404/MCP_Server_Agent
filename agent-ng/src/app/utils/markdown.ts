// Minimal, safe Markdown renderer for agent replies (ported verbatim from the old popup.js).
// Input is HTML-escaped FIRST so raw HTML in a reply can never inject markup — only the tags this
// renderer emits are produced. Supports headings, bold/italic, inline + fenced code, http(s) links,
// bullet/numbered lists and tables.

function escapeHtml(s: string): string {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function renderInline(text: string): string {
  const codeTokens: string[] = [];
  text = text.replace(/`([^`]+)`/g, (_m, c) => {
    codeTokens.push(c);
    return `\u0000C${codeTokens.length - 1}\u0000`;
  });
  text = text.replace(
    /\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g,
    (_m, label, url) => `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`,
  );
  text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  text = text.replace(/__([^_]+)__/g, '<strong>$1</strong>');
  text = text.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
  text = text.replace(/(^|[^_\w])_([^_\n]+)_/g, '$1<em>$2</em>');
  text = text.replace(/\u0000C(\d+)\u0000/g, (_m, i) => `<code>${codeTokens[+i]}</code>`);
  return text;
}

export function renderMarkdown(md: string): string {
  const lines = escapeHtml(md).replace(/\r\n?/g, '\n').split('\n');
  const out: string[] = [];
  let listType: string | null = null;
  const closeList = () => {
    if (listType) {
      out.push(`</${listType}>`);
      listType = null;
    }
  };

  let i = 0;
  while (i < lines.length) {
    const line = lines[i];

    // Fenced code block.
    if (/^\s*```/.test(line)) {
      closeList();
      const code: string[] = [];
      i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) {
        code.push(lines[i]);
        i++;
      }
      i++;
      out.push(`<pre><code>${code.join('\n')}</code></pre>`);
      continue;
    }

    // Table: header row with pipes followed by a |---|---| separator.
    if (
      line.includes('|') &&
      i + 1 < lines.length &&
      /^[\s|:-]+$/.test(lines[i + 1]) &&
      lines[i + 1].includes('-')
    ) {
      closeList();
      const parseRow = (r: string) =>
        r.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim());
      const headers = parseRow(line);
      i += 2;
      let t = '<table><thead><tr>';
      for (const h of headers) t += `<th>${renderInline(h)}</th>`;
      t += '</tr></thead><tbody>';
      while (i < lines.length && lines[i].includes('|') && lines[i].trim() !== '') {
        const cells = parseRow(lines[i]);
        t += '<tr>';
        for (let c = 0; c < headers.length; c++) t += `<td>${renderInline(cells[c] ?? '')}</td>`;
        t += '</tr>';
        i++;
      }
      t += '</tbody></table>';
      out.push(t);
      continue;
    }

    // Heading.
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      closeList();
      const level = heading[1].length;
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
      i++;
      continue;
    }

    // Unordered list item.
    const ul = /^\s*[-*+]\s+(.*)$/.exec(line);
    if (ul) {
      if (listType !== 'ul') {
        closeList();
        out.push('<ul>');
        listType = 'ul';
      }
      out.push(`<li>${renderInline(ul[1])}</li>`);
      i++;
      continue;
    }

    // Ordered list item.
    const ol = /^\s*\d+\.\s+(.*)$/.exec(line);
    if (ol) {
      if (listType !== 'ol') {
        closeList();
        out.push('<ol>');
        listType = 'ol';
      }
      out.push(`<li>${renderInline(ol[1])}</li>`);
      i++;
      continue;
    }

    // Blank line.
    if (line.trim() === '') {
      closeList();
      i++;
      continue;
    }

    // Paragraph: gather consecutive plain lines.
    closeList();
    const para = [line];
    i++;
    while (
      i < lines.length &&
      lines[i].trim() !== '' &&
      !/^\s*```/.test(lines[i]) &&
      !/^(#{1,6})\s+/.test(lines[i]) &&
      !/^\s*[-*+]\s+/.test(lines[i]) &&
      !/^\s*\d+\.\s+/.test(lines[i]) &&
      !lines[i].includes('|')
    ) {
      para.push(lines[i]);
      i++;
    }
    out.push(`<p>${renderInline(para.join('<br>'))}</p>`);
  }

  closeList();
  return out.join('');
}
