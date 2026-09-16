// ═══════════════════════════════════════════════════
// search.js — Search bar + kind filter + result list
// ═══════════════════════════════════════════════════
const Search = (() => {
  let currentKind = null;
  let debounceTimer = null;

  function init() {
    const input = document.getElementById('search-input');
    input.addEventListener('input', () => {
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(() => doSearch(), 300);
    });
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter') { clearTimeout(debounceTimer); doSearch(); }
    });

    document.querySelectorAll('.kind-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const kind = btn.dataset.kind;
        if (currentKind === kind) {
          currentKind = null;
          btn.classList.remove('active');
        } else {
          document.querySelectorAll('.kind-btn').forEach(b => b.classList.remove('active'));
          currentKind = kind;
          btn.classList.add('active');
        }
        doSearch();
      });
    });
  }

  async function doSearch() {
    const q = document.getElementById('search-input').value.trim();
    if (!q) { renderResults([]); return; }

    const status = document.getElementById('search-status');
    status.textContent = 'Searching...';

    try {
      const data = await API.search(q, currentKind, 30);
      renderResults(data.results);
      status.textContent = `${data.totalFound} results in ${data.queryTime}`;
    } catch (e) {
      status.textContent = `Error: ${e.message}`;
    }
  }

  function renderResults(results) {
    const container = document.getElementById('search-results');
    if (!results.length) {
      container.innerHTML = '<div style="padding:20px;text-align:center;color:var(--ctp-overlay0)">No results</div>';
      return;
    }

    container.innerHTML = results.map((r, i) => {
      const badge = r.kind === 'csharp'
        ? '<span class="sr-badge badge-cs">C#</span>'
        : '<span class="sr-badge badge-xml">XML</span>';
      const title = escapeHtml(r.title || r.symbolId);
      const meta = escapeHtml(r.namespace ? `${r.namespace}.${r.containingType || ''}` : r.path);
      return `<div class="search-result" data-index="${i}" data-symbol="${escapeAttr(r.symbolId)}" data-kind="${r.kind}">
        <div class="sr-title">${badge}${title}</div>
        <div class="sr-meta">${meta} · ${r.symbolKind} · ${r.score}</div>
      </div>`;
    }).join('');

    container.querySelectorAll('.search-result').forEach(el => {
      el.addEventListener('click', () => onResultClick(el, results[+el.dataset.index]));
      el.addEventListener('dblclick', () => onResultDblClick(results[+el.dataset.index]));
    });
  }

  function onResultClick(el, result) {
    document.querySelectorAll('.search-result').forEach(e => e.classList.remove('active'));
    el.classList.add('active');
    CodeViewer.show(result.symbolId);
    Graph.addNode(result.symbolId, result.kind === 'csharp' ? 'Type' : 'XmlDef', true);
  }

  function onResultDblClick(result) {
    Graph.expandNode(result.symbolId);
  }

  function escapeHtml(s) { return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
  function escapeAttr(s) { return s.replace(/"/g,'&quot;'); }

  return { init, doSearch };
})();

