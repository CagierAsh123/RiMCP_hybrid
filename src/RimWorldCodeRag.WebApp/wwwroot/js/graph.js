// ═══════════════════════════════════════════════════
// graph.js — Cytoscape.js dependency graph (core)
// ═══════════════════════════════════════════════════
const Graph = (() => {
  let cy = null;
  const nodeSet = new Set();

  const NODE_COLORS = {
    Type:    '#89b4fa', // blue
    Method:  '#cba6f7', // mauve
    Field:   '#fab387', // peach
    Property:'#94e2d5', // teal
    XmlDef:  '#f9e2af', // yellow
    Unknown: '#6c7086'  // overlay0
  };

  const NODE_SHAPES = {
    Type:    'round-rectangle',
    Method:  'ellipse',
    Field:   'diamond',
    Property:'round-rectangle',
    XmlDef:  'hexagon',
    Unknown: 'ellipse'
  };

  const EDGE_COLORS = {
    Inherits:       '#89b4fa',
    Implements:     '#74c7ec',
    Calls:          '#cba6f7',
    References:     '#585b70',
    XmlInherits:    '#89b4fa',
    XmlReferences:  '#585b70',
    XmlBindsClass:  '#f9e2af',
    XmlUsesComp:    '#fab387',
    CSharpUsedByDef:'#a6e3a1'
  };

  const EDGE_STYLES = {
    Inherits:  'solid', Implements: 'solid', Calls: 'solid',
    References: 'dashed', XmlInherits: 'solid', XmlReferences: 'dashed',
    XmlBindsClass: 'dotted', XmlUsesComp: 'dotted', CSharpUsedByDef: 'dashed'
  };

  function init() {
    // Register fcose layout if available
    if (typeof cytoscapeFcose !== 'undefined') {
      cytoscape.use(cytoscapeFcose);
    }

    // Clear placeholder
    const container = document.getElementById('graph-container');
    container.innerHTML = '';

    cy = cytoscape({
      container: document.getElementById('graph-container'),
      style: [
        { selector: 'node', style: {
          'label': 'data(label)',
          'background-color': 'data(color)',
          'shape': 'data(shape)',
          'color': '#cdd6f4',
          'text-valign': 'bottom',
          'text-halign': 'center',
          'font-size': '10px',
          'text-margin-y': 4,
          'width': 28, 'height': 28,
          'border-width': 2,
          'border-color': 'data(color)',
          'background-opacity': 0.2,
          'text-max-width': '100px',
          'text-wrap': 'ellipsis'
        }},
        { selector: 'node:selected', style: {
          'border-width': 3,
          'background-opacity': 0.5,
          'overlay-opacity': 0.1,
          'overlay-color': '#89b4fa'
        }},
        { selector: 'node.center', style: {
          'width': 36, 'height': 36,
          'border-width': 3,
          'background-opacity': 0.4,
          'font-weight': 'bold',
          'font-size': '11px'
        }},
        { selector: 'edge', style: {
          'width': 1.5,
          'line-color': 'data(color)',
          'target-arrow-color': 'data(color)',
          'target-arrow-shape': 'triangle',
          'curve-style': 'bezier',
          'line-style': 'data(lineStyle)',
          'arrow-scale': 0.8,
          'opacity': 0.7
        }},
        { selector: 'edge:selected', style: { 'width': 2.5, 'opacity': 1 }}
      ],
      layout: { name: 'preset' },
      minZoom: 0.2, maxZoom: 4,
      wheelSensitivity: 0.3
    });

    cy.on('tap', 'node', e => {
      const sym = e.target.data('symbolId');
      if (sym) CodeViewer.show(sym);
    });

    cy.on('dbltap', 'node', e => {
      const sym = e.target.data('symbolId');
      if (sym) expandNode(sym);
    });

    // Toolbar
    document.getElementById('btn-fit').addEventListener('click', () => cy.fit(null, 30));
    document.getElementById('btn-relayout').addEventListener('click', runLayout);
    document.getElementById('btn-clear').addEventListener('click', clear);
    document.getElementById('btn-fullscreen').addEventListener('click', toggleFullscreen);
  }

  function shortLabel(symbolId) {
    if (symbolId.startsWith('xml:')) return symbolId.substring(4);
    const parts = symbolId.split('.');
    return parts.length > 1 ? parts[parts.length - 1] : symbolId;
  }

  function guessKind(symbolId) {
    if (symbolId.startsWith('xml:')) return 'XmlDef';
    if (symbolId.includes('(') || symbolId.includes(' ')) return 'Method';
    return 'Type';
  }

  function addNode(symbolId, kind, isCenter) {
    if (nodeSet.has(symbolId)) {
      if (isCenter) cy.getElementById(symbolId).addClass('center');
      return;
    }
    nodeSet.add(symbolId);
    const k = kind || guessKind(symbolId);
    cy.add({
      group: 'nodes',
      data: {
        id: symbolId,
        symbolId,
        label: shortLabel(symbolId),
        color: NODE_COLORS[k] || NODE_COLORS.Unknown,
        shape: NODE_SHAPES[k] || NODE_SHAPES.Unknown,
        kind: k
      },
      classes: isCenter ? 'center' : ''
    });
    updateInfo();
  }

  function addEdge(source, target, edgeKind) {
    const id = `${source}->${target}:${edgeKind}`;
    if (cy.getElementById(id).length > 0) return;
    cy.add({
      group: 'edges',
      data: {
        id,
        source,
        target,
        edgeKind,
        color: EDGE_COLORS[edgeKind] || '#585b70',
        lineStyle: EDGE_STYLES[edgeKind] || 'solid'
      }
    });
  }

  async function expandNode(symbolId) {
    addNode(symbolId, null, true);
    const info = document.getElementById('graph-info');
    info.textContent = `Expanding ${shortLabel(symbolId)}...`;

    try {
      const [uses, usedBy] = await Promise.all([
        API.graphUses(symbolId),
        API.graphUsedBy(symbolId)
      ]);

      (uses.edges || []).forEach(e => {
        addNode(e.targetSymbol);
        addEdge(symbolId, e.targetSymbol, e.edgeKind);
      });

      (usedBy.edges || []).forEach(e => {
        addNode(e.targetSymbol);
        addEdge(e.targetSymbol, symbolId, e.edgeKind);
      });

      runLayout();
      info.textContent = `${cy.nodes().length} nodes · ${cy.edges().length} edges`;
    } catch (e) {
      info.textContent = `Error: ${e.message}`;
    }
  }

  function runLayout() {
    if (cy.nodes().length === 0) return;
    const layout = cy.layout({
      name: typeof cytoscapeFcose !== 'undefined' ? 'fcose' : 'cose',
      animate: true,
      animationDuration: 500,
      randomize: true,
      nodeDimensionsIncludeLabels: true,
      idealEdgeLength: 120,
      nodeRepulsion: 8000,
      gravity: 0.25,
      gravityRange: 3.8
    });
    layout.run();
  }

  function clear() {
    cy.elements().remove();
    nodeSet.clear();
    updateInfo();
  }

  function updateInfo() {
    const info = document.getElementById('graph-info');
    info.textContent = `${cy.nodes().length} nodes · ${cy.edges().length} edges`;
  }

  function toggleFullscreen() {
    const panel = document.getElementById('graph-panel');
    if (panel.style.position === 'fixed') {
      panel.style.position = '';
      panel.style.inset = '';
      panel.style.zIndex = '';
    } else {
      panel.style.position = 'fixed';
      panel.style.inset = '0';
      panel.style.zIndex = '50';
    }
    setTimeout(() => cy.resize(), 100);
  }

  return { init, addNode, addEdge, expandNode, clear, runLayout };
})();
