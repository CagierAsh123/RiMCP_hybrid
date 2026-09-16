// ═══════════════════════════════════════════════════
// code-viewer.js — Prism.js syntax highlighted code
// ═══════════════════════════════════════════════════
const CodeViewer = (() => {
  async function show(symbolId) {
    const header = document.getElementById('code-header');
    const content = document.getElementById('code-content');

    header.innerHTML = `<div class="ch-symbol">${esc(symbolId)}</div>
      <div class="ch-path">Loading...</div>`;
    content.innerHTML = '<div class="code-placeholder"><span class="spinner"></span></div>';

    try {
      const data = await API.getItem(symbolId);
      header.innerHTML = `
        <div class="ch-symbol">${esc(data.symbolId)}</div>
        <div class="ch-path">${esc(data.path)} · ${data.symbolKind}${data.namespace ? ' · ' + esc(data.namespace) : ''}${data.truncated ? ` · Showing ${data.displayedLines}/${data.totalLines} lines` : ''}</div>`;

      const lang = data.language === 'csharp' ? 'csharp' : 'markup';
      const code = esc(data.sourceCode || '');
      content.innerHTML = `<pre class="line-numbers"><code class="language-${lang}">${code}</code></pre>`;
      Prism.highlightAllUnder(content);
    } catch (e) {
      content.innerHTML = `<div class="code-placeholder">Error: ${esc(e.message)}</div>`;
    }
  }

  function esc(s) {
    return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  return { show };
})();
