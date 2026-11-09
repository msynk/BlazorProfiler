// Interactions view: one row per operation (user interaction, navigation, or custom operation) with everything that ran
// in its execution context, ranked by cost. The per-operation drawer shows a waterfall of the operation's events and a
// ranked list of the components it rendered, in the spirit of per-commit views in component profilers.
import { h, clear, fmtMs, fmtNum, shortType, emptyState, sortableTable, debounce, color } from './util.js';
import { LANES, laneOf, describeShort } from './timeline.js';
import { inRange } from './views.js';

const KINDS = { eventHandler: 'interaction', navigation: 'navigation', operation: 'custom' };
// Browser interactions are measured in the browser and carry no operation id: they are matched to a handler by time. On
// Blazor Server, browser timestamps are aligned to the server clock with an offset measured at connection time.
const BROWSER_MATCH_TOLERANCE_MS = 50;
const WATERFALL_LIMIT = 400;

/** Groups events into operations. Exported for tests and other views. */
export function buildOperations(ctx, events) {
  const ops = new Map();
  for (const e of events) {
    if (!e.operationId) {
      continue;
    }

    let op = ops.get(e.operationId);
    if (!op) {
      op = {
        id: e.operationId, root: null, events: [], start: Infinity, end: -Infinity,
        renders: 0, renderMs: 0, batches: 0, batchMs: 0, diffed: 0, params: 0, http: 0, httpMs: 0, httpFailed: 0, db: 0, dbMs: 0,
        interop: 0, interopMs: 0, lifecycleMs: 0, errors: 0, circuitId: null,
      };
      ops.set(e.operationId, op);
    }

    op.events.push(e);
    const start = ctx.toMs(e.timestamp);
    const end = ctx.toMs(e.timestamp + (e.duration || 0));
    op.start = Math.min(op.start, start);
    op.end = Math.max(op.end, end);
    op.circuitId = op.circuitId || e.circuitId || null;
    if (e.outcome && e.outcome !== 'Success') {
      op.errors++;
    }

    const ms = ctx.ticksToMs(e.duration || 0);
    switch (e.type) {
      case 'eventHandler':
      case 'navigation':
      case 'operation':
        if (e.correlation === 'Self' && !op.root) {
          op.root = e;
        }
        break;
      case 'componentRender': op.renders++; op.renderMs += ms; break;
      case 'renderBatch': op.batches++; op.batchMs += ms; op.diffed += Math.max(0, e.updatedComponentCount || 0); break;
      case 'parametersSet': op.params++; break;
      case 'lifecycle': op.lifecycleMs += ms; break;
      case 'http': op.http++; op.httpMs += ms; if (e.outcome !== 'Success' || e.statusCode >= 400) { op.httpFailed++; } break;
      case 'jsInterop': op.interop++; op.interopMs += ms; break;
      case 'dbCommand': op.db++; op.dbMs += ms; break;
      default: break;
    }
  }

  const result = [];
  for (const op of ops.values()) {
    const root = op.root;
    op.kind = root ? KINDS[root.type] : 'other';
    op.name = root ? describeShort(root) : `Operation #${op.id}`;
    op.duration = Math.max(0, op.end - op.start);
    op.handlerMs = root && root.type === 'eventHandler' ? ctx.ticksToMs(root.duration || 0) : null;
    op.handlerSyncMs = root && root.type === 'eventHandler' && root.synchronousDuration ? ctx.ticksToMs(root.synchronousDuration) : null;
    result.push(op);
  }

  return result;
}

/** Attaches the longest browser interaction overlapping each handler's start (time-based match, labelled in the UI). */
function matchBrowserInteractions(ctx, ops, events) {
  const browser = events.filter(e => e.type === 'browserInteraction')
    .map(e => ({ e, start: ctx.toMs(e.timestamp), end: ctx.toMs(e.timestamp + e.duration) }));
  if (!browser.length) {
    return;
  }

  for (const op of ops) {
    if (op.kind !== 'interaction') {
      continue;
    }

    const at = ctx.toMs(op.root.timestamp);
    let best = null;
    for (const b of browser) {
      if (b.start - BROWSER_MATCH_TOLERANCE_MS <= at && at <= b.end + BROWSER_MATCH_TOLERANCE_MS && (!best || b.e.duration > best.duration)) {
        best = b.e;
      }
    }

    op.browser = best;
  }
}

function findingsByOperation(report) {
  const map = new Map();
  for (const f of report?.findings || []) {
    if (f.suppression) {
      continue;
    }

    for (const id of f.relatedOperationIds || []) {
      if (!map.has(id)) {
        map.set(id, []);
      }

      map.get(id).push(f);
    }
  }

  return map;
}

export function interactions(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Interactions'));
  const events = inRange(ctx, ctx.state.events);
  const ops = buildOperations(ctx, events);
  if (!ops.length) {
    root.appendChild(emptyState('No operations recorded',
      'Click, type, or navigate in the app. Each event handler, navigation, and Profiler.StartOperation scope becomes an operation that groups everything it caused.'));
    return root;
  }

  matchBrowserInteractions(ctx, ops, events);
  const findings = findingsByOperation(ctx.state.report);
  for (const op of ops) {
    op.findings = findings.get(op.id) || [];
  }

  root.appendChild(h('p', { class: 'muted' },
    'Each row is one operation and everything that ran in its execution context: renders, render batches, HTTP, and interop. ',
    'Duration spans the first to the last related event. "Browser" is the user-perceived latency (input to next paint) of the browser interaction matched to the handler by time.'));

  const search = h('input', { type: 'search', placeholder: 'Filter by trigger…', 'aria-label': 'Filter operations' });
  const kind = h('select', { 'aria-label': 'Operation kind' }, ['all', 'interaction', 'navigation', 'custom'].map(k => h('option', { value: k }, k === 'all' ? 'all kinds' : `${k}s`)));
  const minDuration = h('input', { type: 'number', min: '0', placeholder: 'min ms', 'aria-label': 'Minimum duration in milliseconds', style: { width: '100px' } });
  const withFindings = h('input', { type: 'checkbox' });
  const host = h('div');
  const render = () => {
    const term = search.value.toLowerCase();
    const min = Number(minDuration.value) || 0;
    const rows = ops.filter(op => (!term || op.name.toLowerCase().includes(term))
      && (kind.value === 'all' || op.kind === kind.value)
      && op.duration >= min
      && (!withFindings.checked || op.findings.length));
    clear(host).appendChild(rows.length ? sortableTable({
      label: 'Operations',
      rows,
      maxHeight: 560,
      initialSort: { key: 'duration', dir: -1 },
      columns: [
        { key: 'start', title: 'Start', num: true, render: op => h('span', { class: 'mono' }, fmtMs(op.start)) },
        { key: 'name', title: 'Trigger', render: op => h('span', { title: op.root ? `${op.root.type} #${op.id}` : `#${op.id}` }, op.name, op.errors ? h('span', { class: 'badge sev-High', title: 'Events in this operation failed' }, `${op.errors} error${op.errors > 1 ? 's' : ''}`) : null) },
        { key: 'duration', title: 'Duration', num: true, render: op => fmtMs(op.duration) },
        { key: 'browser', title: 'Browser', num: true, sort: op => (op.browser ? op.browser.duration : -1), render: op => (op.browser ? h('span', { class: ctx.ticksToMs(op.browser.duration) >= 200 ? 'dup' : null, title: 'Input to next paint, matched by time' }, `≈ ${fmtMs(ctx.ticksToMs(op.browser.duration))}`) : '—') },
        { key: 'handlerMs', title: 'Handler', num: true, sort: op => op.handlerMs ?? -1, render: op => (op.handlerMs == null ? '—' : h('span', { title: op.handlerSyncMs != null ? `synchronous ${fmtMs(op.handlerSyncMs)}` : 'synchronous part not measured (instrument the receiving component)' }, fmtMs(op.handlerMs))) },
        { key: 'renders', title: 'Renders', num: true, render: op => fmtNum(op.renders) },
        { key: 'renderMs', title: 'Render time', num: true, render: op => (op.renders ? fmtMs(op.renderMs) : '—') },
        { key: 'batches', title: 'Batches', num: true, render: op => (op.batches ? `${fmtNum(op.batches)} · ${fmtNum(op.diffed)} diffed` : '0') },
        { key: 'http', title: 'HTTP', num: true, render: op => (op.http ? `${fmtNum(op.http)} · ${fmtMs(op.httpMs)}` : '0') },
        { key: 'db', title: 'DB', num: true, render: op => (op.db ? `${fmtNum(op.db)} · ${fmtMs(op.dbMs)}` : '0') },
        { key: 'interop', title: 'Interop', num: true, render: op => fmtNum(op.interop) },
        { key: 'findings', title: 'Findings', num: true, sort: op => op.findings.length, render: op => (op.findings.length ? h('span', { class: 'badge sev-Medium' }, fmtNum(op.findings.length)) : '') },
      ],
      onSelect: op => ctx.openDrawer(op.name, operationDetails(ctx, op)),
    }) : emptyState('No matching operations', 'Change the filters to see more operations.'));
  };

  for (const control of [search, kind, minDuration, withFindings]) {
    control.addEventListener(control === search ? 'input' : 'change', debounce(render, 120));
  }

  render();
  root.appendChild(h('section', { class: 'panel', 'aria-label': 'Operations' },
    h('div', { class: 'panel-head' }, h('h2', null, `Operations (${fmtNum(ops.length)})`)),
    h('div', { class: 'toolbar' }, search, kind, minDuration, h('label', null, withFindings, ' With findings only')),
    host));
  return root;
}

function operationDetails(ctx, op) {
  const sorted = [...op.events].sort((a, b) => a.timestamp - b.timestamp);
  const byComponent = new Map();
  for (const e of sorted) {
    if (e.type !== 'componentRender') {
      continue;
    }

    const entry = byComponent.get(e.componentType) || { type: e.componentType, renders: 0, ms: 0, instances: new Set(), unchanged: 0, reasons: {} };
    entry.renders++;
    entry.ms += ctx.ticksToMs(e.duration);
    entry.instances.add(e.componentInstanceId);
    entry.unchanged += e.outputUnchanged === true ? 1 : 0;
    entry.reasons[e.reason || 'Unknown'] = (entry.reasons[e.reason || 'Unknown'] || 0) + 1;
    byComponent.set(e.componentType, entry);
  }

  const ranked = [...byComponent.values()].sort((a, b) => b.ms - a.ms);
  return h('div', null,
    h('dl', { class: 'kv' },
      h('dt', null, 'Operation'), h('dd', null, `#${op.id} (${op.kind})`),
      h('dt', null, 'Duration'), h('dd', null, `${fmtMs(op.duration)} (first to last related event)`),
      op.handlerMs != null ? [h('dt', null, 'Handler'), h('dd', null, `${fmtMs(op.handlerMs)}${op.handlerSyncMs != null ? ` · synchronous ${fmtMs(op.handlerSyncMs)}` : ''}`)] : null,
      op.browser ? [h('dt', null, 'Browser latency'), h('dd', null, `≈ ${fmtMs(ctx.ticksToMs(op.browser.duration))} (input delay ${fmtMs(op.browser.inputDelayMs)} · processing ${fmtMs(op.browser.processingMs)} · presentation ${fmtMs(op.browser.presentationDelayMs)}); matched by time`)] : null,
      h('dt', null, 'Rendering'), h('dd', null, `${fmtNum(op.renders)} component renders (${fmtMs(op.renderMs)}) · ${fmtNum(op.batches)} batches (${fmtMs(op.batchMs)}) · ${fmtNum(op.diffed)} components diffed`),
      h('dt', null, 'HTTP'), h('dd', null, op.http ? `${fmtNum(op.http)} requests · ${fmtMs(op.httpMs)} total${op.httpFailed ? ` · ${fmtNum(op.httpFailed)} failed` : ''}` : 'none'),
      h('dt', null, 'Database'), h('dd', null, op.db ? `${fmtNum(op.db)} commands · ${fmtMs(op.dbMs)} total` : 'none'),
      h('dt', null, 'JS interop'), h('dd', null, op.interop ? `${fmtNum(op.interop)} calls · ${fmtMs(op.interopMs)} total` : 'none'),
      op.circuitId ? [h('dt', null, 'Circuit'), h('dd', { class: 'mono' }, op.circuitId)] : null),
    op.findings.length
      ? h('div', null, h('h3', null, 'Findings involving this operation'), h('ul', { class: 'compact' }, op.findings.map(f => h('li', null,
        h('span', { class: `badge sev-${f.severity}` }, f.severity), ' ',
        h('a', { href: '#diagnostics', onclick: e => { e.preventDefault(); ctx.navigate('diagnostics', { finding: f.fingerprint }); } }, f.title)))))
      : null,
    ranked.length
      ? h('div', null, h('h3', null, 'Components rendered (ranked by render time)'), rankedChart(ranked))
      : h('p', { class: 'muted' }, op.params ? 'Components received parameters, but none of them is instrumented (inherit ProfiledComponentBase for per-component render data).' : 'No component renders were recorded in this operation.'),
    h('h3', null, `Waterfall (${fmtNum(sorted.length)} events)`),
    waterfall(ctx, op, sorted),
    op.root ? h('p', null, h('button', { type: 'button', class: 'btn', onclick: () => ctx.navigate('timeline', { eventId: op.root.id }) }, 'Show in timeline')) : null);
}

function rankedChart(ranked) {
  const max = Math.max(...ranked.map(r => r.ms), 0.001);
  return h('ul', { class: 'ranked', role: 'list' }, ranked.slice(0, 30).map(r => {
    const reasons = Object.entries(r.reasons).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k} ${v}`).join(', ');
    return h('li', { title: `${r.type}\n${r.renders} renders of ${r.instances.size} instance(s); causes: ${reasons}${r.unchanged ? `; ${r.unchanged} with identical output` : ''}` },
      h('span', { class: 'ranked-label' }, shortType(r.type), h('span', { class: 'muted' }, ` ×${r.renders}${r.unchanged ? ` (${r.unchanged} identical)` : ''}`)),
      h('span', { class: 'ranked-bar' }, h('span', { style: { width: `${Math.max(1, (r.ms / max) * 100)}%`, background: color('--lane-component') } })),
      h('span', { class: 'mono ranked-value' }, fmtMs(r.ms)));
  }));
}

function waterfall(ctx, op, sorted) {
  const span = Math.max(0.001, op.duration);
  const laneColor = new Map(LANES.map(l => [l.id, l.color]));
  const rows = sorted.filter(e => e.type !== 'componentChildren').slice(0, WATERFALL_LIMIT);
  const list = h('div', { class: 'waterfall', role: 'list' });
  for (const e of rows) {
    const start = ctx.toMs(e.timestamp) - op.start;
    const ms = ctx.ticksToMs(e.duration || 0);
    const row = h('button', { type: 'button', class: 'waterfall-row', role: 'listitem', title: `${describeShort(e)} — starts +${fmtMs(start)}, ${fmtMs(ms)}`, onclick: () => ctx.showEvent(e) },
      h('span', { class: 'waterfall-label' }, describeShort(e)),
      h('span', { class: 'waterfall-track' }, h('span', {
        class: e.outcome && e.outcome !== 'Success' ? 'fail' : null,
        style: { left: `${(start / span) * 100}%`, width: `${Math.max(0.4, (ms / span) * 100)}%`, background: color(laneColor.get(laneOf(e)) || '--lane-marker') },
      })),
      h('span', { class: 'mono waterfall-value' }, ms ? fmtMs(ms) : '·'));
    list.appendChild(row);
  }

  if (sorted.length > rows.length) {
    list.appendChild(h('p', { class: 'muted' }, `${fmtNum(sorted.length - rows.length)} more events not shown.`));
  }

  return list;
}
