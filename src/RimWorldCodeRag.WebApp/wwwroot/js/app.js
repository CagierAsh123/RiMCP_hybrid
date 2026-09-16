// ═══════════════════════════════════════════════════
// app.js — Main controller: routing + event coordination
// ═══════════════════════════════════════════════════
const App = (() => {
  function init() {
    // Navigation
    document.querySelectorAll('.nav-btn[data-page]').forEach(btn => {
      btn.addEventListener('click', () => navigate(btn.dataset.page));
    });

    // Initialize modules
    Search.init();
    Graph.init();
    Settings.init();

    // Default page
    navigate('search');

    // Focus search on Ctrl+K
    document.addEventListener('keydown', e => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        navigate('search');
        document.getElementById('search-input').focus();
      }
    });
  }

  function navigate(page) {
    document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));

    const btn = document.querySelector(`.nav-btn[data-page="${page}"]`);
    const pageEl = document.getElementById(`${page}-page`);
    if (btn) btn.classList.add('active');
    if (pageEl) pageEl.classList.add('active');
  }

  return { init, navigate };
})();

document.addEventListener('DOMContentLoaded', App.init);
