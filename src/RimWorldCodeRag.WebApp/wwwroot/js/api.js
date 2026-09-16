// ═══════════════════════════════════════════════════
// api.js — Fetch wrapper for all API calls
// ═══════════════════════════════════════════════════
const API = {
  async get(url) {
    const resp = await fetch(url);
    if (!resp.ok) throw new Error(`${resp.status}: ${await resp.text()}`);
    return resp.json();
  },

  async post(url, body) {
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!resp.ok) throw new Error(`${resp.status}: ${await resp.text()}`);
    return resp.json();
  },

  async put(url, body) {
    const resp = await fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!resp.ok) throw new Error(`${resp.status}: ${await resp.text()}`);
    return resp.json();
  },

  search(q, kind, max) {
    const params = new URLSearchParams({ q });
    if (kind) params.set('kind', kind);
    if (max) params.set('max', max);
    return this.get(`/api/search?${params}`);
  },

  graphUses(symbol, kind, depth) {
    const params = new URLSearchParams({ symbol });
    if (kind) params.set('kind', kind);
    if (depth) params.set('depth', depth);
    return this.get(`/api/graph/uses?${params}`);
  },

  graphUsedBy(symbol, kind, depth) {
    const params = new URLSearchParams({ symbol });
    if (kind) params.set('kind', kind);
    if (depth) params.set('depth', depth);
    return this.get(`/api/graph/used-by?${params}`);
  },

  getItem(symbol, maxLines) {
    const params = new URLSearchParams({ symbol });
    if (maxLines) params.set('maxLines', maxLines);
    return this.get(`/api/item?${params}`);
  },

  indexInfo: ()    => API.get('/api/index/info'),
  indexStatus: ()  => API.get('/api/index/status'),
  indexBuild: (b)  => API.post('/api/index/build', b),
  indexPath: (p)   => API.post('/api/index/path', p),

  getConfig: ()    => API.get('/api/config'),
  saveConfig: (c)  => API.put('/api/config', c),
  mcpSnippet: ()   => API.get('/api/config/mcp-snippet'),

  getModels: ()    => API.get('/api/models'),
  embeddingStatus: () => API.get('/api/models/embedding-server'),
  startEmbedding: () => API.post('/api/models/embedding-server/start', {}),
  stopEmbedding: ()  => API.post('/api/models/embedding-server/stop', {})
};
