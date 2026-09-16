// ═══════════════════════════════════════════════════
// indexer.js — Index build UI + SSE log stream
// ═══════════════════════════════════════════════════
const Indexer = (() => {
  let eventSource = null;

  function showBuildDialog() {
    const overlay = document.createElement('div');
    overlay.className = 'dialog-overlay';
    overlay.innerHTML = `
      <div class="dialog">
        <h3>Build Index</h3>
        <div class="setting-row">
          <label>Source Root</label>
          <input type="text" id="build-source" placeholder="C:/path/to/RimWorld/Source">
        </div>
        <div class="setting-row">
          <label>Force Rebuild</label>
          <select id="build-force">
            <option value="none">None (incremental)</option>
            <option value="all">All</option>
            <option value="lucene">Lucene only</option>
            <option value="embed">Embeddings only</option>
            <option value="graph">Graph only</option>
          </select>
        </div>
        <div class="dialog-actions">
          <button class="btn btn-secondary" onclick="this.closest('.dialog-overlay').remove()">Cancel</button>
          <button class="btn btn-primary" onclick="Indexer.startBuild()">Start Build</button>
        </div>
      </div>`;
    document.body.appendChild(overlay);
  }

  async function startBuild() {
    const sourceRoot = document.getElementById('build-source').value.trim();
    const force = document.getElementById('build-force').value;

    document.querySelector('.dialog-overlay')?.remove();

    if (!sourceRoot) { toast('Source root is required'); return; }

    try {
      await API.indexBuild({ sourceRoot, force });
      document.getElementById('build-status').textContent = 'Building...';
      document.getElementById('build-log').textContent = '';
      document.getElementById('build-progress').style.display = 'block';
      startLogStream();
    } catch (e) { toast('Error: ' + e.message); }
  }

  function startLogStream() {
    if (eventSource) eventSource.close();
    const logEl = document.getElementById('build-log');
    logEl.style.display = 'block';

    eventSource = new EventSource('/api/index/log');
    eventSource.onmessage = (e) => {
      if (e.data === '[DONE]') {
        eventSource.close();
        eventSource = null;
        document.getElementById('build-status').textContent = 'Completed';
        Settings.loadIndexInfo();
        return;
      }
      logEl.textContent += e.data + '\n';
      logEl.scrollTop = logEl.scrollHeight;
    };
    eventSource.onerror = () => {
      eventSource.close();
      eventSource = null;
    };
  }

  function toast(msg) {
    const el = document.createElement('div');
    el.className = 'toast';
    el.textContent = msg;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 3000);
  }

  return { showBuildDialog, startBuild };
})();
