// Latency heatmap of operations (time × duration) and "what differs in the selection": select the slow cells (drag a
// rectangle, or "Slowest 10%") and the attributes that are over-represented in the selected operations compared with the
// rest are ranked, in the spirit of Honeycomb's BubbleUp. Attributes are facts recorded for each operation: its trigger,
// the component types it rendered, the endpoints, queries, and interop identifiers it used, its circuit, errors, findings.
import { h, clear, fmtMs, fmtPct, shortType } from './util.js';

const COLUMNS = 60;
const ROWS = 12;
const WIDTH = 720;
const HEIGHT = 180;
const LEFT = 56;
const BOTTOM = 18;

/** The attributes of an operation, as "kind: value" strings. */
export function attributesOf(op) {
  const attributes = new Set([`trigger: ${op.name}`, `kind: ${op.kind}`]);
  for (const e of op.events) {
    switch (e.type) {
      case 'componentRender':
      case 'parametersSet':
        if (e.componentType) {
          attributes.add(`component: ${shortType(e.componentType)}`);
        }

        break;
      case 'http':
        attributes.add(`endpoint: ${e.endpoint || e.url || '?'}`);
        if (e.outcome !== 'Success' || e.statusCode >= 400) {
          attributes.add('HTTP failure');
        }

        break;
      case 'dbCommand':
        attributes.add(`query: ${(e.commandText || '?').slice(0, 80)}`);
        break;
      case 'jsInterop':
        attributes.add(`interop: ${e.identifier || '?'}`);
        break;
      default:
        break;
    }
  }

  if (op.circuitId) {
    attributes.add(`circuit: ${op.circuitId.slice(0, 12)}`);
  }

  if (op.errors) {
    attributes.add('has errors');
  }

  if (op.findings && op.findings.length) {
    for (const f of op.findings) {
      attributes.add(`finding: ${f.ruleId}`);
    }
  }

  return attributes;
}

/**
 * Ranks attributes by how much more often they occur in the selected operations than in the others. Returns rows with
 * the share in each group and the difference, largest positive difference first.
 */
export function compareAttributes(selected, rest) {
  const count = list => {
    const counts = new Map();
    for (const op of list) {
      for (const a of attributesOf(op)) {
        counts.set(a, (counts.get(a) || 0) + 1);
      }
    }

    return counts;
  };

  const inSelected = count(selected);
  const inRest = count(rest);
  const rows = [];
  for (const [attribute, n] of inSelected) {
    const selectedShare = n / Math.max(1, selected.length);
    const restShare = (inRest.get(attribute) || 0) / Math.max(1, rest.length);
    rows.push({ attribute, selectedShare, restShare, difference: selectedShare - restShare, selectedCount: n });
  }

  // Attributes shared by every operation explain nothing; the others are ordered by how much they distinguish the selection.
  return rows
    .filter(r => r.difference > 0.0001 || rest.length === 0)
    .sort((a, b) => b.difference - a.difference || b.selectedCount - a.selectedCount);
}

export function latencyHeatmap(ops) {
  const host = h('section', { class: 'panel', 'aria-label': 'Latency heatmap' });
  host.appendChild(h('h2', null, 'Latency heatmap'));
  const timed = ops.filter(op => op.duration > 0);
  if (timed.length < 5) {
    host.appendChild(h('p', { class: 'muted' }, 'At least five operations with a duration are needed.'));
    return host;
  }

  // Loops instead of Math.min(...list): spreading a very large list exceeds the call stack.
  let t0 = Infinity;
  let t1 = -Infinity;
  let shortest = Infinity;
  let longest = 0;
  for (const o of timed) {
    t0 = Math.min(t0, o.start);
    t1 = Math.max(t1, o.start);
    shortest = Math.min(shortest, o.duration);
    longest = Math.max(longest, o.duration);
  }

  t1 += 1;
  const minLog = Math.log10(Math.max(0.1, shortest));
  const maxLog = Math.log10(longest) + 0.0001;
  const col = op => Math.min(COLUMNS - 1, Math.floor(((op.start - t0) / (t1 - t0)) * COLUMNS));
  const row = op => Math.min(ROWS - 1, Math.floor(((Math.log10(Math.max(0.1, op.duration)) - minLog) / Math.max(0.0001, maxLog - minLog)) * ROWS));
  const cells = new Map();
  for (const op of timed) {
    const key = `${col(op)},${row(op)}`;
    cells.set(key, (cells.get(key) || 0) + 1);
  }

  let max = 1;
  for (const n of cells.values()) {
    max = Math.max(max, n);
  }
  const cw = (WIDTH - LEFT) / COLUMNS;
  const ch = (HEIGHT - BOTTOM) / ROWS;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${WIDTH} ${HEIGHT}`);
  svg.setAttribute('class', 'heatmap');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', `Operations by start time and duration (${timed.length} operations); drag to select`);
  for (const [key, n] of cells) {
    const [c, r] = key.split(',').map(Number);
    const rect = document.createElementNS(ns, 'rect');
    rect.setAttribute('x', String(LEFT + (c * cw)));
    rect.setAttribute('y', String((ROWS - 1 - r) * ch));
    rect.setAttribute('width', String(Math.max(1, cw - 1)));
    rect.setAttribute('height', String(Math.max(1, ch - 1)));
    rect.setAttribute('class', 'heat-cell');
    rect.style.opacity = String(0.2 + (0.8 * n) / max);
    const title = document.createElementNS(ns, 'title');
    title.textContent = `${n} operation(s)`;
    rect.appendChild(title);
    svg.appendChild(rect);
  }

  for (const r of [0, Math.floor(ROWS / 2), ROWS - 1]) {
    const label = document.createElementNS(ns, 'text');
    label.setAttribute('x', '2');
    label.setAttribute('y', String(((ROWS - 1 - r) * ch) + (ch * 0.8)));
    label.setAttribute('class', 'heat-axis');
    label.textContent = fmtMs(10 ** (minLog + ((r + 0.5) / ROWS) * (maxLog - minLog)));
    svg.appendChild(label);
  }

  const brush = document.createElementNS(ns, 'rect');
  brush.setAttribute('class', 'heat-brush');
  brush.setAttribute('visibility', 'hidden');
  svg.appendChild(brush);

  const result = h('div', { class: 'heat-result', role: 'region', 'aria-live': 'polite' });
  const select = (predicate, label) => {
    // Only plotted operations (with a duration) take part, on both sides.
    const chosen = new Set(timed.filter(predicate));
    const selected = [...chosen];
    const rest = timed.filter(op => !chosen.has(op));
    clear(result);
    if (!selected.length) {
      result.appendChild(h('p', { class: 'muted' }, 'No operations in the selection.'));
      return;
    }

    const rows = compareAttributes(selected, rest).slice(0, 12);
    result.appendChild(h('p', null, `${label}: ${selected.length} of ${timed.length} operations. What they have in common more often than the others:`));
    result.appendChild(rows.length ? h('table', { 'aria-label': 'Attributes over-represented in the selection' },
      h('thead', null, h('tr', null, h('th', null, 'Attribute'), h('th', { class: 'num' }, 'Selected'), h('th', { class: 'num' }, 'Others'), h('th', null, ''))),
      h('tbody', null, rows.map(r => h('tr', null,
        h('td', { class: 'mono' }, r.attribute),
        h('td', { class: 'num' }, fmtPct(r.selectedShare)),
        h('td', { class: 'num' }, fmtPct(r.restShare)),
        h('td', null, h('span', { class: 'heat-bar', style: { width: `${Math.round(r.difference * 100)}px` } })))))) : h('p', { class: 'muted' }, 'Nothing distinguishes them from the other operations.'));
  };

  // Drag a rectangle over cells (time × duration) to select operations.
  const point = e => {
    const box = svg.getBoundingClientRect();
    return { x: ((e.clientX - box.left) / box.width) * WIDTH, y: ((e.clientY - box.top) / box.height) * HEIGHT };
  };
  let start = null;
  svg.addEventListener('mousedown', e => {
    start = point(e);
    e.preventDefault();
  });
  svg.addEventListener('mousemove', e => {
    if (!start) {
      return;
    }

    const p = point(e);
    brush.setAttribute('x', String(Math.min(start.x, p.x)));
    brush.setAttribute('y', String(Math.min(start.y, p.y)));
    brush.setAttribute('width', String(Math.abs(p.x - start.x)));
    brush.setAttribute('height', String(Math.abs(p.y - start.y)));
    brush.setAttribute('visibility', 'visible');
  });
  const finish = e => {
    if (!start) {
      return;
    }

    const p = point(e);
    const c0 = Math.floor((Math.min(start.x, p.x) - LEFT) / cw);
    const c1 = Math.floor((Math.max(start.x, p.x) - LEFT) / cw);
    const r1 = ROWS - 1 - Math.floor(Math.min(start.y, p.y) / ch);
    const r0 = ROWS - 1 - Math.floor(Math.max(start.y, p.y) / ch);
    start = null;
    select(op => col(op) >= c0 && col(op) <= c1 && row(op) >= r0 && row(op) <= r1, 'Selected cells');
  };
  svg.addEventListener('mouseup', finish);
  svg.addEventListener('mouseleave', e => {
    if (start) {
      finish(e);
    }
  });

  // The slowest tenth by rank (at least one operation), so ties at the boundary cannot select far more than 10%.
  const slowest = new Set([...timed].sort((a, b) => b.duration - a.duration).slice(0, Math.max(1, Math.ceil(timed.length * 0.1))));
  let cutoff = Infinity;
  for (const o of slowest) {
    cutoff = Math.min(cutoff, o.duration);
  }
  host.appendChild(h('div', { class: 'toolbar' },
    h('button', { type: 'button', class: 'btn', id: 'heat-slowest', onclick: () => select(op => slowest.has(op), `Slowest ${slowest.size} operation(s) (${fmtMs(cutoff)} or more)`) }, 'Explain the slowest 10%'),
    h('span', { class: 'muted' }, 'Drag over the heatmap to select operations by start time and duration.')));
  host.appendChild(svg);
  host.appendChild(result);
  return host;
}
