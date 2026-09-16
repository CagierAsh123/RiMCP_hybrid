// ═══════════════════════════════════════════════════
// settings.js — Settings page (index, MCP config, models)
// ═══════════════════════════════════════════════════
const Settings = (() => {
  function init() {
    document.querySelectorAll('.settings-tab').forEach(tab => {
      tab.addEventListener('click', () => {
        document.querySelectorAll('.settings-tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
        tab.classList.add('active');
        document.getElementById(tab.dataset.tab).classList.add('active');
      });
    });
    loadIndexInfo();
    loadConfig();
    loadModels();
  }

  // ── Tab 1: Index Management ──
  async function loadIndexInfo() {
    try {
      const info = await API.indexInfo();
      document.getElementById('idx-root').textContent = info.indexRoot;
      document.getElementById('idx-exists').textContent = info.exists ? 'Yes' : 'No';

      const grid = document.getElementById('idx-stats');
      grid.innerHTML = `
        <div class="stat-card"><div class="stat-label">Lucene Files</div>
          <div class="stat-value">${info.lucene.exists ? info.lucene.fileCount : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Lucene Size</div>
          <div class="stat-value">${info.lucene.exists ? info.lucene.sizeMB + ' MB' : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Vector Files</div>
          <div class="stat-value">${info.vectors.exists ? info.vectors.fileCount : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Vector Size</div>
          <div class="stat-value">${info.vectors.exists ? info.vectors.sizeMB + ' MB' : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Graph Files</div>
          <div class="stat-value">${info.graph.exists ? info.graph.fileCount : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Graph Size</div>
          <div class="stat-value">${info.graph.exists ? info.graph.sizeMB + ' MB' : '—'}</div></div>
        <div class="stat-card"><div class="stat-label">Last Modified</div>
          <div class="stat-value" style="font-size:13px">${info.lastModified ? new Date(info.lastModified).toLocaleString() : '—'}</div></div>`;
    } catch (e) {
      document.getElementById('idx-stats').innerHTML = `<div style="color:var(--ctp-red)">Error: ${e.message}</div>`;
    }
  }

  // ── Tab 2: MCP Config ──
  async function loadConfig() {
    try {
      const cfg = await API.getConfig();
      document.getElementById('cfg-indexRoot').value = cfg.indexRoot || '';
      document.getElementById('cfg-embeddingUrl').value = cfg.embeddingServerUrl || '';
      document.getElementById('cfg-apiKey').value = cfg.apiKey === '***' ? '' : (cfg.apiKey || '');
      document.getElementById('cfg-modelName').value = cfg.modelName || '';
      document.getElementById('cfg-port').value = cfg.webPort || 5800;
    } catch (e) { console.error('loadConfig', e); }
  }

  async function saveConfig() {
    try {
      await API.saveConfig({
        indexRoot: document.getElementById('cfg-indexRoot').value,
        embeddingServerUrl: document.getElementById('cfg-embeddingUrl').value,
        apiKey: document.getElementById('cfg-apiKey').value,
        modelName: document.getElementById('cfg-modelName').value,
        webPort: parseInt(document.getElementById('cfg-port').value) || 5800
      });
      toast('Configuration saved');
    } catch (e) { toast('Error: ' + e.message); }
  }

  async function generateSnippet() {
    try {
      const data = await API.mcpSnippet();
      const box = document.getElementById('mcp-snippet-output');
      box.innerHTML = `
        <div style="margin-bottom:12px">
          <strong>Claude Desktop</strong>
          <div class="snippet-box"><button class="copy-btn" onclick="Settings.copySnippet(this)">Copy</button><code>${JSON.stringify(data.claudeDesktop, null, 2)}</code></div>
        </div>
        <div>
          <strong>VS Code</strong>
          <div class="snippet-box"><button class="copy-btn" onclick="Settings.copySnippet(this)">Copy</button><code>${JSON.stringify(data.vscode, null, 2)}</code></div>
        </div>`;
    } catch (e) { toast('Error: ' + e.message); }
  }

  function copySnippet(btn) {
    const code = btn.parentElement.querySelector('code').textContent;
    navigator.clipboard.writeText(code).then(() => {
      btn.textContent = 'Copied!';
      setTimeout(() => btn.textContent = 'Copy', 1500);
    });
  }

  // ── Tab 3: Models ──
  async function loadModels() {
    try {
      const data = await API.getModels();
      const list = document.getElementById('model-list');
      if (data.models.length === 0) {
        list.innerHTML = '<div style="color:var(--ctp-overlay0);padding:12px">No models found in ./models/</div>';
      } else {
        list.innerHTML = data.models.map(m => `
          <div class="model-card">
            <div>
              <div class="model-name">${m.name}</div>
              <div class="model-info">${m.path} · ${m.sizeMB} MB</div>
            </div>
          </div>`).join('');
      }

      const running = data.embeddingServer.running;
      document.getElementById('embed-status').innerHTML =
        `<span class="status-dot ${running ? 'green' : 'red'}"></span>${running ? 'Running' : 'Stopped'}`;
      document.getElementById('embed-url').textContent = data.embeddingServer.url;
      document.getElementById('btn-start-embed').disabled = running;
      document.getElementById('btn-stop-embed').disabled = !running;
    } catch (e) { console.error('loadModels', e); }
  }

  async function startEmbedding() {
    try {
      await API.startEmbedding();
      toast('Embedding server starting...');
      setTimeout(loadModels, 2000);
    } catch (e) { toast('Error: ' + e.message); }
  }

  async function stopEmbedding() {
    try {
      await API.stopEmbedding();
      toast('Embedding server stopped');
      loadModels();
    } catch (e) { toast('Error: ' + e.message); }
  }

  function toast(msg) {
    const el = document.createElement('div');
    el.className = 'toast';
    el.textContent = msg;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 3000);
  }

  return { init, loadIndexInfo, saveConfig, generateSnippet, copySnippet, loadModels, startEmbedding, stopEmbedding };
})();
