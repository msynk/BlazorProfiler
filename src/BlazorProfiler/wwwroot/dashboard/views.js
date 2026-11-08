// Dashboard views. Each view receives the shared context and returns a DOM element.
import {
  h, clear, fmtMs, fmtBytes, fmtNum, fmtPct, shortType, qualityBadge, emptyState, virtualList, sortableTable,
  lineChart, color, settings, debounce, safeWebUrl,
} from './util.js';
import { Timeline, LANES, describeShort } from './timeline.js';

const SEVERITIES = ['Critical', 'High', 'Medium', 'Low', 'Info'];

function d(summary) {
  return summary || { count: 0, totalMs: 0, meanMs: 0, p50Ms: 0, p95Ms: 0, maxMs: 0 };
}

/** The events within the configured time range. Always returns a new array, so callers may keep it while polling appends to the source. */
export function inRange(ctx, events) {
  if (!settings.rangeSeconds) {
    return events.slice();
  }

  const cutoff = ctx.maxMs() - settings.rangeSeconds * 1000;
  return events.filter(e => ctx.toMs(e.timestamp + (e.duration || 0)) >= cutoff);
}

function seriesInRange(points) {
  if (!points || !settings.rangeSeconds || !points.length) {
    return points || [];
  }

  const last = points[points.length - 1].second;
  return points.filter(p => p.second > last - settings.rangeSeconds);
}

function tile(label, value, sub, title) {
  return h('div', { class: 'tile', title: title || null }, h('div', { class: 'label' }, label), h('div', { class: 'value' }, value), sub ? h('div', { class: 'sub' }, sub) : null);
}

function panel(title, ...content) {
  return h('section', { class: 'panel', 'aria-label': title }, h('div', { class: 'panel-head' }, h('h2', null, title)), ...content);
}

function needsSummary(ctx) {
  if (!ctx.state.summary) {
    return h('div', { class: 'loading', role: 'status' }, ctx.state.error ? ctx.state.error : 'Loading session data…');
  }

  return null;
}

// ----------------------------------------------------------------------------------------------- Overview

export function overview(ctx) {
  const pending = needsSummary(ctx);
  if (pending) {
    return pending;
  }

  const s = ctx.state.summary;
  const health = ctx.state.info?.health;
  const batches = d(s.renderBatches?.durations);
  const handlers = s.handlers || [];
  const handlerCount = handlers.reduce((n, x) => n + x.durations.count, 0);
  const handlerP95 = Math.max(0, ...handlers.map(x => x.durations.p95Ms));
  const endpoints = s.endpoints || [];
  const requests = endpoints.reduce((n, x) => n + x.durations.count, 0);
  const failures = endpoints.reduce((n, x) => n + x.failures, 0);
  const interop = (s.interop || []).reduce((n, x) => n + x.durations.count, 0);
  const runtime = s.runtime || {};
  const session = ctx.state.session;
  const findings = (ctx.state.report?.findings || []).filter(f => !f.suppression);

  const root = h('div');
  root.appendChild(h('h1', null, 'Overview'));
  if (s.containsSampledData) {
    root.appendChild(h('div', { class: 'banner warn', role: 'note' }, 'Some event kinds are sampled; counts and totals are weighted estimates.'));
  }

  root.appendChild(h('div', { class: 'tiles' },
    tile('Session duration', fmtMs(session?.durationMs), `${fmtNum(session?.storedEvents)} events stored`),
    tile('Render batches', fmtNum(batches.count), `p95 ${fmtMs(batches.p95Ms)} · max ${fmtMs(batches.maxMs)}`),
    tile('Components updated', fmtNum(s.renderBatches?.totalUpdatedComponents), `largest batch ${fmtNum(s.renderBatches?.maxUpdatedComponents)}`),
    tile('Event handlers', fmtNum(handlerCount), `p95 ${fmtMs(handlerP95)}`),
    tile('HTTP requests', fmtNum(requests), failures ? `${fmtNum(failures)} failed` : 'no failures'),
    tile('JS interop calls', fmtNum(interop)),
    tile('Allocated', runtime.samples ? fmtBytes(runtime.allocatedBytes) : '—', !runtime.samples ? 'runtime collector off' : runtime.gcPauseUnavailable ? `${fmtNum(runtime.gen0Collections + runtime.gen1Collections + runtime.gen2Collections)} GCs` : `GC pauses ${fmtMs(runtime.gcPauseMs)}`),
    tile('Circuits', fmtNum((s.circuits || []).length), (s.circuits || []).length ? `${fmtNum(s.circuits.filter(c => c.connected && !c.closed).length)} connected` : 'not a server session'),
    tile('Browser interactions', fmtNum(s.browserInteractions?.count), s.browserInteractions?.count ? `p95 ${fmtMs(s.browserInteractions.p95Ms)}` : 'needs BlazorProfilerClient'),
    tile('Dropped events', fmtNum(health ? health.droppedQueueFull : s.droppedEventsReported), health ? `profiler overhead ${fmtNum(health.emitMicrosecondsPerEvent, 2)} µs/event` : null, 'Events dropped because the profiler queue was full.'),
  ));

  const top = findings.slice(0, 5);
  root.appendChild(panel('Highest-impact findings',
    top.length
      ? h('ul', { class: 'compact', role: 'list' }, top.map(f => h('li', null,
        h('span', { class: `badge sev-${f.severity}` }, f.severity), ' ',
        h('a', { href: '#diagnostics', onclick: e => { e.preventDefault(); ctx.navigate('diagnostics', { finding: f.fingerprint }); } }, f.title),
        h('span', { class: 'muted' }, ` · ${f.confidence} confidence`))))
      : emptyState('No findings', ctx.state.report ? 'No rule reported a problem for this session.' : 'Findings are computed periodically.'),
  ));

  const slowest = [...(s.components || [])]
    .map(c => ({ ...c, cost: c.renders.totalMs || c.parametersSets.totalMs }))
    .sort((a, b) => b.cost - a.cost).slice(0, 8);
  const ts = seriesInRange(s.timeSeries);
  root.appendChild(h('div', { class: 'grid cols-2' },
    panel('Slowest components',
      slowest.length ? sortableTable({
        label: 'Slowest components',
        rows: slowest,
        initialSort: { key: 'cost', dir: -1 },
        columns: [
          { key: 'componentType', title: 'Component', render: r => h('span', { title: r.componentType }, shortType(r.componentType)) },
          { key: 'cost', title: 'Time', num: true, render: r => h('span', { title: r.instrumented ? 'BuildRenderTree time' : 'SetParametersAsync time (includes awaited work)' }, fmtMs(r.cost)) },
          { key: 'renders', title: 'Renders', num: true, sort: r => r.renders.count, render: r => r.instrumented ? fmtNum(r.renders.count) : h('span', { class: 'muted', title: 'Requires ProfiledComponentBase' }, 'n/a') },
          { key: 'params', title: 'Param updates', num: true, sort: r => r.parametersSets.count, render: r => fmtNum(r.parametersSets.count) },
        ],
        onSelect: r => ctx.navigate('components', { component: r.componentType }),
      }) : emptyState('No component data yet', 'Interact with the app to collect rendering data.')),
    panel('Activity per second',
      lineChart({ title: 'Render batches / handlers / HTTP', series: [
        { label: 'batches', color: color('--lane-batch'), values: ts.map(p => ({ x: p.second, y: p.renderBatches })) },
        { label: 'handlers', color: color('--lane-interaction'), values: ts.map(p => ({ x: p.second, y: p.eventHandlers })) },
        { label: 'http', color: color('--lane-http'), values: ts.map(p => ({ x: p.second, y: p.httpRequests })) },
      ] }),
      lineChart({ title: 'Render batch time per second', yFormat: v => fmtMs(v), series: [
        { label: 'batch ms', color: color('--lane-batch'), values: ts.map(p => ({ x: p.second, y: p.renderBatchMs })) },
        { label: 'handler ms', color: color('--lane-interaction'), values: ts.map(p => ({ x: p.second, y: p.eventHandlerMs })) },
      ] })),
  ));

  return root;
}

// ----------------------------------------------------------------------------------------------- Components

export function components(ctx, params) {
  const pending = needsSummary(ctx);
  if (pending) {
    return pending;
  }

  const s = ctx.state.summary;
  const root = h('div');
  root.appendChild(h('h1', null, 'Components'));
  const anyInstrumented = s.components.some(c => c.instrumented);
  if (!anyInstrumented) {
    root.appendChild(h('div', { class: 'banner', role: 'note' },
      'Per-component renders, lifecycle timing, parameter-change detection, and the component tree require ProfiledComponentBase. ',
      'Add ', h('code', null, '@inherits BlazorProfiler.Instrumentation.ProfiledComponentBase'), ' to _Imports.razor. ',
      'Without it, parameter updates per component type are measured from framework metrics.'));
  }

  const search = h('input', { type: 'search', placeholder: 'Filter components…', 'aria-label': 'Filter components', value: params?.component ? shortType(params.component) : '' });
  const tableHost = h('div');
  const render = () => {
    const term = search.value.toLowerCase();
    const rows = s.components.filter(c => !term || c.componentType.toLowerCase().includes(term)).map(c => ({
      ...c,
      unchanged: c.parametersSets.count ? (c.parametersUnchanged + c.parametersChangedByReferenceOnly) / c.parametersSets.count : null,
      sameOutput: c.rendersWithOutputComparison ? c.rendersWithUnchangedOutput / c.rendersWithOutputComparison : null,
    }));
    clear(tableHost).appendChild(sortableTable({
      label: 'Components',
      rows,
      initialSort: { key: 'renderTotal', dir: -1 },
      columns: [
        { key: 'componentType', title: 'Component', render: r => h('span', { title: r.componentType }, shortType(r.componentType), r.instrumented ? h('span', { class: 'badge measured', title: 'Instrumented with ProfiledComponentBase' }, 'instrumented') : null) },
        { key: 'created', title: 'Created', num: true, render: r => fmtNum(r.created) },
        { key: 'renderCount', title: 'Renders', num: true, sort: r => r.renders.count, render: r => r.instrumented ? fmtNum(r.renders.count) : 'n/a' },
        { key: 'renderTotal', title: 'Render time', num: true, sort: r => r.renders.totalMs || r.parametersSets.totalMs, render: r => r.instrumented ? fmtMs(r.renders.totalMs) : 'n/a' },
        { key: 'renderP95', title: 'Render p95', num: true, sort: r => r.renders.p95Ms, render: r => r.instrumented ? fmtMs(r.renders.p95Ms) : 'n/a' },
        { key: 'params', title: 'Param updates', num: true, sort: r => r.parametersSets.count, render: r => fmtNum(r.parametersSets.count) },
        { key: 'paramsP95', title: 'Params p95', num: true, sort: r => r.parametersSets.p95Ms, render: r => fmtMs(r.parametersSets.p95Ms) },
        { key: 'unchanged', title: 'Unchanged params', num: true, render: r => (r.unchanged == null || !r.instrumented ? '—' : fmtPct(r.unchanged)) },
        { key: 'sameOutput', title: 'Identical output', num: true, render: r => (r.sameOutput == null ? '—' : fmtPct(r.sameOutput)) },
        { key: 'errors', title: 'Errors', num: true },
      ],
      onSelect: r => ctx.openDrawer(shortType(r.componentType), componentDetails(ctx, r)),
    }));
  };
  search.addEventListener('input', debounce(render, 150));
  render();

  root.appendChild(panel('Component types', h('div', { class: 'toolbar' }, search), tableHost));
  root.appendChild(panel('Component tree', componentTree(ctx, s)));
  if (params?.component) {
    const match = s.components.find(c => c.componentType === params.component);
    if (match) {
      queueMicrotask(() => ctx.openDrawer(shortType(match.componentType), componentDetails(ctx, match)));
    }
  }

  return root;
}

function componentDetails(ctx, c) {
  const lifecycle = Object.entries(c.lifecycle || {});
  const instances = (ctx.state.summary.instances || []).filter(i => i.componentType === c.componentType);
  return h('div', null,
    h('p', { class: 'mono' }, c.componentType),
    h('dl', { class: 'kv' },
      h('dt', null, 'Instrumented'), h('dd', null, c.instrumented ? 'yes (ProfiledComponentBase)' : 'no (framework metrics only)'),
      h('dt', null, 'Instances created'), h('dd', null, fmtNum(c.created)),
      h('dt', null, 'Instances disposed'), h('dd', null, c.instrumented ? fmtNum(c.disposed) : 'unavailable'),
      h('dt', null, 'Renders'), h('dd', null, c.instrumented ? `${fmtNum(c.renders.count)} · total ${fmtMs(c.renders.totalMs)} · p95 ${fmtMs(c.renders.p95Ms)} · max ${fmtMs(c.renders.maxMs)}` : 'unavailable'),
      h('dt', null, 'Identical-output renders'), h('dd', null, c.rendersWithOutputComparison ? `${fmtNum(c.rendersWithUnchangedOutput)} of ${fmtNum(c.rendersWithOutputComparison)}` : 'not compared (enable TrackRenderOutput)'),
      h('dt', null, 'Parameter updates'), h('dd', null, `${fmtNum(c.parametersSets.count)} · p95 ${fmtMs(c.parametersSets.p95Ms)} · max ${fmtMs(c.parametersSets.maxMs)}`),
      h('dt', null, 'Unchanged / reference-only'), h('dd', null, c.instrumented ? `${fmtNum(c.parametersUnchanged)} / ${fmtNum(c.parametersChangedByReferenceOnly)}` : 'unavailable'),
      h('dt', null, 'Ignored render requests'), h('dd', null, c.instrumented ? `${fmtNum(c.renderRequestsCoalesced)} coalesced (render already pending) · ${fmtNum(c.renderRequestsSuppressed)} suppressed by ShouldRender` : 'unavailable'),
      h('dt', null, 'Event handlers'), h('dd', null, `${fmtNum(c.eventHandlers.count)} · p95 ${fmtMs(c.eventHandlers.p95Ms)}`),
    ),
    Object.keys(c.referenceOnlyParameters || {}).length
      ? h('div', null, h('h3', null, 'Parameters changed by reference only'),
        h('ul', { class: 'compact' }, Object.entries(c.referenceOnlyParameters).sort((a, b) => b[1] - a[1]).map(([name, n]) => h('li', null, `${name} — ${fmtNum(n)}×`))))
      : null,
    lifecycle.length
      ? h('div', null, h('h3', null, 'Lifecycle'), sortableTable({
        label: 'Lifecycle',
        rows: lifecycle.map(([method, stats]) => ({ method, ...stats })),
        initialSort: { key: 'totalMs', dir: -1 },
        columns: [
          { key: 'method', title: 'Method' },
          { key: 'count', title: 'Calls', num: true, render: r => fmtNum(r.count) },
          { key: 'totalMs', title: 'Total', num: true, render: r => fmtMs(r.totalMs) },
          { key: 'p95Ms', title: 'p95', num: true, render: r => fmtMs(r.p95Ms) },
          { key: 'maxMs', title: 'Max', num: true, render: r => fmtMs(r.maxMs) },
        ],
      }))
      : null,
    instances.length
      ? h('div', null, h('h3', null, `Instances (${instances.length})`), sortableTable({
        label: 'Instances',
        maxHeight: 260,
        rows: instances.map(i => ({ ...i, lifetime: i.disposedTimestamp && i.createdTimestamp ? ctx.ticksToMs(i.disposedTimestamp - i.createdTimestamp) : null })),
        initialSort: { key: 'renders', dir: -1 },
        columns: [
          { key: 'instanceId', title: 'Id', num: true },
          { key: 'renders', title: 'Renders', num: true },
          { key: 'renderMs', title: 'Render time', num: true, render: r => fmtMs(r.renderMs) },
          { key: 'unchangedParametersSets', title: 'Unchanged params', num: true },
          { key: 'lifetime', title: 'Lifetime', num: true, render: r => (r.disposedTimestamp ? fmtMs(r.lifetime) : 'alive') },
        ],
      }))
      : null,
    h('p', null, h('button', { type: 'button', class: 'btn', onclick: () => ctx.navigate('timeline', { search: shortType(c.componentType) }) }, 'Show in timeline')),
  );
}

function componentTree(ctx, s) {
  const instances = s.instances || [];
  const withParents = instances.filter(i => i.parentInstanceId);
  if (!withParents.length) {
    return emptyState('No tree data', 'The component tree is built from instrumented parents (ProfiledComponentBase with TrackComponentTree).');
  }

  const byId = new Map(instances.map(i => [i.instanceId, i]));
  const children = new Map();
  for (const i of instances) {
    const parent = i.parentInstanceId && byId.has(i.parentInstanceId) ? i.parentInstanceId : 0;
    if (!children.has(parent)) {
      children.set(parent, []);
    }

    children.get(parent).push(i);
  }

  const LIMIT = 500;
  const kidsOf = id => children.get(id) || [];
  // An expandable node: the children list is built on first expansion, so large trees stay cheap.
  const branch = (labelText, detail, title, className, buildChildren) => {
    const li = h('li', { role: 'treeitem', 'aria-expanded': 'false' });
    const marker = h('span', { 'aria-hidden': 'true' }, '▸ ');
    li.appendChild(h('button', {
      type: 'button',
      class: className,
      title,
      onclick: () => {
        const open = li.getAttribute('aria-expanded') === 'true';
        li.setAttribute('aria-expanded', open ? 'false' : 'true');
        marker.textContent = open ? '▸ ' : '▾ ';
        if (open) {
          li.querySelector(':scope > ul')?.remove();
        } else {
          li.appendChild(buildChildren());
        }
      },
    }, marker, labelText, h('span', { class: 'muted' }, detail)));
    return li;
  };

  const nodeItem = node => {
    const kids = kidsOf(node.instanceId);
    const label = `${shortType(node.componentType)} #${node.instanceId}`;
    const detail = ` · ${node.renders} renders · ${fmtMs(node.renderMs)}${kids.length ? ` · ${kids.length} children` : ''}`;
    const className = `node${node.disposedTimestamp ? ' disposed' : ''}`;
    if (kids.length) {
      return branch(label, detail, node.componentType, className, () => level(kids, 'group'));
    }

    return h('li', { role: 'treeitem' }, h('button', { type: 'button', class: className, title: node.componentType },
      h('span', { 'aria-hidden': 'true' }, '• '), label, h('span', { class: 'muted' }, detail)));
  };

  const level = (nodes, role) => {
    const list = h('ul', { class: role === 'tree' ? 'tree root' : 'tree', role });
    for (const node of nodes.slice(0, LIMIT)) {
      list.appendChild(nodeItem(node));
    }

    if (nodes.length > LIMIT) {
      list.appendChild(h('li', { class: 'muted' }, `… ${fmtNum(nodes.length - LIMIT)} more not shown`));
    }

    return list;
  };

  // Roots with tracked children first. Childless roots are usually prerendered instances (static rendering never runs
  // OnAfterRender, where children are discovered) or children of non-instrumented components: list them separately so
  // they do not bury the real hierarchy.
  const roots = kidsOf(0);
  const parents = roots.filter(n => kidsOf(n.instanceId).length);
  const loose = roots.filter(n => !kidsOf(n.instanceId).length);
  const tree = level(parents, 'tree');
  if (loose.length) {
    tree.appendChild(branch(`${fmtNum(loose.length)} instance(s) without a tracked parent`,
      ' · prerendered, children of non-instrumented components, or not rendered interactively yet',
      'Children are discovered after interactive renders of instrumented parents (OnAfterRender), which static prerendering never runs.',
      'node', () => level(loose, 'group')));
  }

  return tree;
}

// ----------------------------------------------------------------------------------------------- Timeline

export function timeline(ctx, params) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Timeline'));
  const events = inRange(ctx, ctx.state.events);
  if (!events.length) {
    root.appendChild(emptyState('No events yet', 'Interact with the application to record activity.'));
    return root;
  }

  const details = h('div');
  const rangeHost = h('div');
  const tl = new Timeline({
    onSelect: e => ctx.showEvent(e),
    onRange: range => {
      clear(rangeHost);
      if (!range) {
        return;
      }

      const inside = tl.events.filter(e => e.__e >= range.start && e.__s <= range.end);
      rangeHost.appendChild(panel(`Selection: ${fmtMs(range.end - range.start)}, ${fmtNum(inside.length)} events`, eventList(ctx, inside, 300)));
    },
  });
  ctx.registerTimeline(tl);

  const search = h('input', { type: 'search', placeholder: 'Search events…', 'aria-label': 'Search events', value: params?.search || '' });
  search.addEventListener('input', debounce(() => {
    tl.search = search.value;
    tl.draw();
  }, 150));
  tl.search = search.value;

  const followButton = h('button', { type: 'button', class: 'btn', 'aria-pressed': 'true', onclick: () => {
    tl.follow = !tl.follow;
    followButton.setAttribute('aria-pressed', String(tl.follow));
    tl.draw();
  } }, 'Follow live');

  const lanes = h('div', { class: 'lane-toggles', role: 'group', 'aria-label': 'Lanes' }, LANES.map(lane => {
    const box = h('input', { type: 'checkbox', checked: true, onchange: () => {
      if (box.checked) {
        tl.visible.add(lane.id);
      } else {
        tl.visible.delete(lane.id);
      }

      tl.draw();
    } });
    return h('label', null, box, h('span', { class: 'swatch', style: { background: color(lane.color) } }), lane.label);
  }));

  root.appendChild(panel('Activity',
    h('div', { class: 'toolbar' },
      search,
      h('button', { type: 'button', class: 'btn', onclick: () => tl.zoom(0.5), 'aria-label': 'Zoom in' }, '+'),
      h('button', { type: 'button', class: 'btn', onclick: () => tl.zoom(2), 'aria-label': 'Zoom out' }, '−'),
      h('button', { type: 'button', class: 'btn', onclick: () => tl.fit() }, 'Fit'),
      followButton,
      h('span', { class: 'muted' }, 'Drag to pan · wheel to zoom · Shift+drag to select · click an event for details. Outlined events share the selected operation (causal links); temporal overlap alone is never highlighted.')),
    lanes,
    tl.element,
    details));
  root.appendChild(rangeHost);
  tl.setData(events, ctx.toMs);
  if (params?.eventId) {
    const target = events.find(e => e.id === params.eventId);
    if (target) {
      tl.focusEvent(target);
      ctx.showEvent(target);
    }
  } else if (!ctx.state.live) {
    tl.fit();
  }

  return root;
}

export function eventList(ctx, events, height = 420, { compact = false } = {}) {
  if (compact) {
    // For narrow hosts such as the details drawer: the description gets the space instead of fixed-width columns.
    return virtualList({
      rows: events,
      label: 'Events',
      height,
      columns: '72px minmax(0,1fr) 76px',
      header: ['Start', 'Event', 'Duration'],
      renderRow: e => [
        h('span', { class: 'mono' }, fmtMs(ctx.toMs(e.timestamp))),
        h('span', { title: `${e.type}: ${describeShort(e)}${e.correlation ? ` (${e.correlation})` : ''}` }, h('span', { class: 'muted' }, `${e.type} · `), describeShort(e)),
        h('span', { class: 'mono' }, e.duration ? fmtMs(ctx.ticksToMs(e.duration)) : '—'),
      ],
      onSelect: e => ctx.showEvent(e),
    });
  }

  return virtualList({
    rows: events,
    label: 'Events',
    height,
    columns: '90px 120px minmax(0,1fr) 90px 110px',
    header: ['Start', 'Kind', 'Description', 'Duration', 'Correlation'],
    renderRow: e => [
      h('span', { class: 'mono' }, fmtMs(ctx.toMs(e.timestamp))),
      h('span', null, e.type),
      h('span', { title: describeShort(e) }, describeShort(e)),
      h('span', { class: 'mono' }, e.duration ? fmtMs(ctx.ticksToMs(e.duration)) : '—'),
      h('span', { class: 'muted' }, e.operationId ? `${e.correlation || ''} #${e.operationId}` : '—'),
    ],
    onSelect: e => ctx.showEvent(e),
  });
}

// ----------------------------------------------------------------------------------------------- Network

export function network(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Network'));
  const requests = inRange(ctx, ctx.state.events.filter(e => e.type === 'http'));
  const resources = inRange(ctx, ctx.state.events.filter(e => e.type === 'browserResource'));
  if (!requests.length && !resources.length) {
    root.appendChild(emptyState('No requests recorded', 'HttpClient requests through IHttpClientFactory (or ProfilerHttpMessageHandler) and browser resource timing appear here.'));
    return root;
  }

  // Mark duplicates: same URL hash requested again within 2 s.
  const lastByHash = new Map();
  for (const r of [...requests].sort((a, b) => a.timestamp - b.timestamp)) {
    const previous = lastByHash.get(r.urlHash);
    r.__dup = previous !== undefined && ctx.ticksToMs(r.timestamp - previous) <= 2000 && (r.method === 'GET' || r.method === 'HEAD');
    lastByHash.set(r.urlHash, r.timestamp);
  }

  const filterText = h('input', { type: 'search', placeholder: 'URL or endpoint contains…', 'aria-label': 'Filter by URL' });
  const status = h('select', { 'aria-label': 'Status filter' }, ['all', '2xx', '3xx', '4xx', '5xx', 'failed', 'duplicates'].map(v => h('option', { value: v }, v)));
  const minDuration = h('input', { type: 'number', min: '0', placeholder: 'min ms', 'aria-label': 'Minimum duration in milliseconds', style: { width: '100px' } });
  const components = [...new Set(requests.map(r => r.originComponentType).filter(Boolean))];
  const component = h('select', { 'aria-label': 'Originating component' }, h('option', { value: '' }, 'any component'), components.map(c => h('option', { value: c }, shortType(c))));
  const listHost = h('div');
  const minStart = Math.min(...requests.map(r => ctx.toMs(r.timestamp)));
  const maxEnd = Math.max(...requests.map(r => ctx.toMs(r.timestamp + r.duration)));

  const render = () => {
    const text = filterText.value.toLowerCase();
    const min = Number(minDuration.value) || 0;
    const rows = requests.filter(r => {
      if (text && !(r.url || '').toLowerCase().includes(text) && !(r.endpoint || '').toLowerCase().includes(text)) {
        return false;
      }

      if (min && ctx.ticksToMs(r.duration) < min) {
        return false;
      }

      if (component.value && r.originComponentType !== component.value) {
        return false;
      }

      switch (status.value) {
        case '2xx': return r.statusCode >= 200 && r.statusCode < 300;
        case '3xx': return r.statusCode >= 300 && r.statusCode < 400;
        case '4xx': return r.statusCode >= 400 && r.statusCode < 500;
        case '5xx': return r.statusCode >= 500;
        case 'failed': return r.outcome !== 'Success';
        case 'duplicates': return r.__dup;
        default: return true;
      }
    }).sort((a, b) => a.timestamp - b.timestamp);

    const span = Math.max(1, maxEnd - minStart);
    clear(listHost).appendChild(virtualList({
      rows,
      label: 'Requests',
      columns: '80px 56px minmax(0,2fr) 56px 80px 80px minmax(0,1fr) 160px',
      header: ['Start', 'Method', 'URL', 'Status', 'Duration', 'Size', 'Origin', 'Waterfall'],
      renderRow: r => {
        const start = ctx.toMs(r.timestamp) - minStart;
        const duration = ctx.ticksToMs(r.duration);
        return [
          h('span', { class: 'mono' }, fmtMs(ctx.toMs(r.timestamp))),
          h('span', null, r.method),
          h('span', { title: r.url }, r.__dup ? h('span', { class: 'dup', title: 'Duplicate: identical URL requested within 2 s' }, '⧉ ') : null, r.url),
          h('span', { class: r.outcome === 'Success' ? null : 'dup' }, r.statusCode ?? r.outcome),
          h('span', { class: 'mono' }, fmtMs(duration)),
          h('span', { title: r.responseBytesQuality }, r.responseBytes != null ? fmtBytes(r.responseBytes) : '—', r.fromCache ? ' (cache)' : ''),
          h('span', { title: r.originComponentType || '' }, r.originComponentType ? `${shortType(r.originComponentType)}${r.originLifecycleMethod ? `.${r.originLifecycleMethod}` : ''}` : '—'),
          h('div', { class: 'bar-cell' }, h('span', { class: r.outcome === 'Success' ? '' : 'fail', style: { left: `${(start / span) * 100}%`, width: `${Math.max(1, (duration / span) * 100)}%` } })),
        ];
      },
      onSelect: r => ctx.showEvent(r),
    }));
  };

  for (const control of [filterText, status, minDuration, component]) {
    control.addEventListener('input', debounce(render, 120));
  }

  render();
  root.appendChild(panel(`Requests (${fmtNum(requests.length)})`, requests.length
    ? [h('div', { class: 'toolbar' }, filterText, status, minDuration, component), listHost]
    : emptyState('No HttpClient requests', 'Only browser resource timing was recorded (see below).')));

  const endpoints = ctx.state.summary?.endpoints || [];
  root.appendChild(panel('Endpoints', sortableTable({
    label: 'Endpoints',
    rows: endpoints,
    initialSort: { key: 'total', dir: -1 },
    columns: [
      { key: 'endpoint', title: 'Endpoint', render: r => h('span', { class: 'mono' }, r.endpoint) },
      { key: 'count', title: 'Requests', num: true, sort: r => r.durations.count, render: r => fmtNum(r.durations.count) },
      { key: 'failures', title: 'Failures', num: true, render: r => fmtNum(r.failures) },
      { key: 'p50', title: 'p50', num: true, sort: r => r.durations.p50Ms, render: r => fmtMs(r.durations.p50Ms) },
      { key: 'p95', title: 'p95', num: true, sort: r => r.durations.p95Ms, render: r => fmtMs(r.durations.p95Ms) },
      { key: 'total', title: 'Total', num: true, sort: r => r.durations.totalMs, render: r => fmtMs(r.durations.totalMs) },
      { key: 'responseBytes', title: 'Bytes', num: true, render: r => (r.responseBytes ? fmtBytes(r.responseBytes) : '—') },
      { key: 'cacheHits', title: 'Cache hits', num: true, render: r => fmtNum(r.cacheHits) },
    ],
    onSelect: r => {
      filterText.value = r.endpoint.replace(/^[A-Z]+ /, '').replace(/\{[a-z]+\}.*$/, '');
      render();
    },
  })));

  if (resources.length) {
    root.appendChild(panel(`Other browser requests (${fmtNum(resources.length)})`,
      h('p', { class: 'muted' }, 'Requests that did not go through HttpClient (Resource Timing). In WebAssembly, entries matching an HttpClient request are merged into that request instead.'),
      virtualList({
        rows: resources,
        label: 'Browser resources',
        height: 300,
        columns: '80px 90px minmax(0,1fr) 80px 90px',
        header: ['Start', 'Initiator', 'URL', 'Duration', 'Transfer'],
        renderRow: r => [
          h('span', { class: 'mono' }, fmtMs(ctx.toMs(r.timestamp))),
          h('span', null, r.initiatorType),
          h('span', { title: r.url }, r.url),
          h('span', { class: 'mono' }, fmtMs(ctx.ticksToMs(r.duration))),
          h('span', null, r.fromCache ? 'cache' : fmtBytes(r.transferSize)),
        ],
        onSelect: r => ctx.showEvent(r),
      })));
  }

  return root;
}

// ----------------------------------------------------------------------------------------------- Interop

export function interop(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'JavaScript interop'));
  const calls = inRange(ctx, ctx.state.events.filter(e => e.type === 'jsInterop'));
  const summary = ctx.state.summary?.interop || [];
  if (!calls.length && !summary.length) {
    root.appendChild(emptyState('No interop calls recorded',
      ctx.state.info?.source === 'server'
        ? 'On Blazor Server, inject IProfiledJSRuntime instead of IJSRuntime in the components you want to profile (the framework requires the original runtime for circuits).'
        : 'In WebAssembly, IJSRuntime calls are recorded automatically. Calls on imported modules are recorded when Collectors.JSObjectReferences is enabled.'));
    return root;
  }

  root.appendChild(panel('By identifier', sortableTable({
    label: 'Interop identifiers',
    rows: summary,
    initialSort: { key: 'count', dir: -1 },
    columns: [
      { key: 'identifier', title: 'Identifier', render: r => h('span', { class: 'mono' }, `${r.direction === 'JSToDotNet' ? '←' : '→'} ${r.identifier}`) },
      { key: 'count', title: 'Calls', num: true, sort: r => r.durations.count, render: r => fmtNum(r.durations.count) },
      { key: 'failures', title: 'Failures', num: true, render: r => fmtNum(r.failures) },
      { key: 'p95', title: 'p95', num: true, sort: r => r.durations.p95Ms, render: r => fmtMs(r.durations.p95Ms) },
      { key: 'total', title: 'Total', num: true, sort: r => r.durations.totalMs, render: r => fmtMs(r.durations.totalMs) },
      { key: 'maxPayloadBytes', title: 'Max payload', num: true, render: r => (r.maxPayloadBytes ? `≈ ${fmtBytes(r.maxPayloadBytes)}` : '—') },
    ],
  })));

  root.appendChild(panel(`Calls (${fmtNum(calls.length)})`, virtualList({
    rows: calls,
    label: 'Interop calls',
    columns: '80px minmax(0,2fr) 60px 80px 90px minmax(0,1fr)',
    header: ['Start', 'Identifier', 'Sync', 'Duration', 'Payload', 'Origin'],
    renderRow: c => [
      h('span', { class: 'mono' }, fmtMs(ctx.toMs(c.timestamp))),
      h('span', { class: 'mono', title: c.identifier }, `${c.viaObjectReference ? 'module.' : ''}${c.identifier}`),
      h('span', null, c.synchronous ? 'sync' : 'async'),
      h('span', { class: 'mono' }, fmtMs(ctx.ticksToMs(c.duration))),
      h('span', { title: 'Estimated serialized size' }, c.payloadBytes != null ? `≈ ${fmtBytes(c.payloadBytes)}` : '—'),
      h('span', null, c.originComponentType ? shortType(c.originComponentType) : '—'),
    ],
    onSelect: c => ctx.showEvent(c),
  })));
  return root;
}

// ----------------------------------------------------------------------------------------------- Runtime

export function runtime(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Runtime'));
  const samples = inRange(ctx, ctx.state.events.filter(e => e.type === 'runtime'));
  const r = ctx.state.summary?.runtime || {};
  if (!samples.length) {
    root.appendChild(emptyState('No runtime samples', 'Enable Collectors.Runtime to sample allocation, heap, and GC data.'));
    return root;
  }

  const browser = ctx.state.info?.source === 'webassembly' || samples.every(s => s.workingSetBytes == null);
  const last = samples[samples.length - 1];
  root.appendChild(h('div', { class: 'banner', role: 'note' }, browser
    ? 'WebAssembly: values describe the .NET runtime inside this browser tab — not the JavaScript heap and not a server process. CPU, working set, and thread-pool data are unavailable in the browser.'
    : 'Server: values describe the whole server process and are shared by every circuit. Allocations are never attributed to individual components.'));
  root.appendChild(h('div', { class: 'tiles' },
    tile('Heap size', fmtBytes(last.heapSizeBytes ?? last.totalMemoryBytes)),
    tile('Allocated (session)', fmtBytes(r.allocatedBytes)),
    tile('GC collections', `${fmtNum(r.gen0Collections)} / ${fmtNum(r.gen1Collections)} / ${fmtNum(r.gen2Collections)}`, 'gen0 / gen1 / gen2'),
    r.gcPauseUnavailable
      ? tile('GC pause time', 'unavailable', 'not reported by the WebAssembly runtime')
      : tile('GC pause time', fmtMs(r.gcPauseMs), r.sampledMs ? `${fmtPct(r.gcPauseMs / r.sampledMs)} of sampled time` : null),
    tile('Working set', browser ? 'unavailable' : fmtBytes(last.workingSetBytes)),
    tile('Thread pool', browser ? 'unavailable' : `${fmtNum(last.threadPoolThreads)} threads`, browser ? null : `${fmtNum(last.threadPoolQueueLength)} queued`),
  ));

  const rate = s => (s.allocatedBytes ?? 0) / Math.max(1e-9, ctx.ticksToMs(s.duration) / 1000);
  const x = s => ctx.toMs(s.timestamp + s.duration) / 1000;
  const charts = [
    lineChart({ title: 'Heap size', yFormat: fmtBytes, series: [{ label: 'heap', color: color('--lane-component'), values: samples.map(s => ({ x: x(s), y: s.heapSizeBytes ?? s.totalMemoryBytes ?? 0 })) }] }),
    lineChart({ title: 'Allocation rate (per second)', yFormat: fmtBytes, series: [{ label: 'alloc/s', color: color('--lane-interop'), values: samples.map(s => ({ x: x(s), y: rate(s) })) }] }),
    lineChart({ title: 'Garbage collections per sample', bars: true, series: [
      { label: 'gen0', color: color('--lane-batch'), values: samples.map(s => ({ x: x(s), y: s.gen0Collections ?? 0 })) },
      { label: 'gen2', color: color('--bad'), values: samples.map(s => ({ x: x(s), y: s.gen2Collections ?? 0 })) },
    ] }),
  ];
  if (!browser) {
    charts.push(lineChart({ title: 'CPU usage (process)', yFormat: v => fmtPct(v), series: [{ label: 'cpu', color: color('--lane-browser'), values: samples.map(s => ({ x: x(s), y: (s.cpuTicks ?? 0) / Math.max(1, s.duration) / Math.max(1, s.processorCount || 1) })) }] }));
    charts.push(lineChart({ title: 'Thread-pool queue length', series: [{ label: 'queued', color: color('--lane-circuit'), values: samples.map(s => ({ x: x(s), y: s.threadPoolQueueLength ?? 0 })) }] }));
  }

  root.appendChild(h('div', { class: 'grid cols-2' }, charts));
  return root;
}

// ----------------------------------------------------------------------------------------------- Circuits

export function circuits(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Circuits'));
  const summary = ctx.state.summary?.circuits || [];
  const events = inRange(ctx, ctx.state.events.filter(e => e.type === 'circuit' || e.type === 'circuitActivity'));
  if (!summary.length && !events.length) {
    root.appendChild(emptyState('No circuit data', 'Circuit data is available for Blazor Server and Interactive Server render modes.'));
    return root;
  }

  root.appendChild(panel('Circuits', sortableTable({
    label: 'Circuits',
    rows: summary,
    initialSort: { key: 'activities', dir: -1 },
    columns: [
      { key: 'circuitId', title: 'Circuit', render: c => h('span', { class: 'mono' }, c.circuitId) },
      { key: 'state', title: 'State', sort: c => (c.closed ? 0 : c.connected ? 2 : 1), render: c => (c.closed ? 'closed' : c.connected ? 'connected' : 'disconnected') },
      { key: 'connectionDowns', title: 'Drops', num: true },
      { key: 'activities', title: 'Inbound activities', num: true, sort: c => c.inboundActivities.count, render: c => fmtNum(c.inboundActivities.count) },
      { key: 'activityP95', title: 'Activity p95', num: true, sort: c => c.inboundActivities.p95Ms, render: c => fmtMs(c.inboundActivities.p95Ms) },
      { key: 'batches', title: 'Render batches', num: true, sort: c => c.renderBatches.count, render: c => fmtNum(c.renderBatches.count) },
      { key: 'batchP95', title: 'Round trip p95', num: true, sort: c => c.renderBatches.p95Ms, render: c => h('span', { title: 'Diff + transport + browser apply, until acknowledgement' }, fmtMs(c.renderBatches.p95Ms)) },
    ],
  }), h('p', { class: 'muted' }, 'Render-batch durations on circuits include the network round trip and browser-side application; they are not the same as diff generation time or paint time.')));

  const byName = new Map();
  for (const e of events.filter(e => e.type === 'circuitActivity')) {
    const key = e.activityName || '(unknown)';
    const entry = byName.get(key) || { name: key, count: 0, total: 0, max: 0 };
    const ms = ctx.ticksToMs(e.duration);
    entry.count++;
    entry.total += ms;
    entry.max = Math.max(entry.max, ms);
    byName.set(key, entry);
  }

  root.appendChild(panel('Inbound activities by hub method', sortableTable({
    label: 'Inbound activities',
    rows: [...byName.values()],
    initialSort: { key: 'total', dir: -1 },
    columns: [
      { key: 'name', title: 'Hub method', render: r => h('span', { class: 'mono' }, r.name) },
      { key: 'count', title: 'Count', num: true },
      { key: 'total', title: 'Total', num: true, render: r => fmtMs(r.total) },
      { key: 'max', title: 'Max', num: true, render: r => fmtMs(r.max) },
    ],
  })));
  root.appendChild(panel('Circuit events', eventList(ctx, events, 300)));

  if (ctx.state.source?.kind === 'api') {
    const live = h('div', null, h('div', { class: 'loading' }, 'Loading live circuits…'));
    ctx.state.source.circuits().then(list => {
      clear(live).appendChild(list.length
        ? sortableTable({
          label: 'Live circuits',
          rows: list,
          columns: [
            { key: 'circuitId', title: 'Circuit', render: c => h('span', { class: 'mono' }, c.circuitId) },
            { key: 'connected', title: 'Connected', render: c => (c.connected ? 'yes' : 'no') },
            { key: 'authenticated', title: 'User', render: c => (c.authenticated ? 'authenticated' : 'anonymous') },
            { key: 'openedAt', title: 'Opened', render: c => new Date(c.openedAt).toLocaleTimeString() },
          ],
        })
        : emptyState('No live circuits visible to you'));
    }).catch(error => clear(live).appendChild(emptyState('Unavailable', error.message)));
    root.appendChild(panel('Live circuits (this server)', live));
  }

  return root;
}

// ----------------------------------------------------------------------------------------------- Diagnostics

export function diagnostics(ctx, params) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Diagnostics'));
  const report = ctx.state.report;
  if (!report) {
    root.appendChild(h('div', { class: 'loading', role: 'status' }, 'Analyzing session…'));
    return root;
  }

  if (report.fromTrace) {
    root.appendChild(h('div', { class: 'banner imported', role: 'note' }, 'Findings were computed when this trace was exported. Import the trace into a running profiler to re-analyze it with current rules and thresholds.'));
  }

  const showSuppressed = h('input', { type: 'checkbox' });
  const severity = h('select', { 'aria-label': 'Minimum severity' }, SEVERITIES.map(s => h('option', { value: s }, `${s} and above`)));
  severity.value = 'Info';
  const list = h('div');
  const render = () => {
    clear(list);
    const minIndex = SEVERITIES.indexOf(severity.value);
    const findings = report.findings.filter(f => (showSuppressed.checked || !f.suppression) && SEVERITIES.indexOf(f.severity) <= minIndex);
    if (!findings.length) {
      list.appendChild(emptyState('No findings', 'No rule reported a problem with the current filters. Rules only report when evidence is sufficient; see the rule list below for what was evaluated.'));
      return;
    }

    for (const f of findings) {
      list.appendChild(findingCard(ctx, f, params?.finding === f.fingerprint));
    }
  };
  showSuppressed.addEventListener('change', render);
  severity.addEventListener('change', render);
  render();

  root.appendChild(panel(`Findings (${report.findings.filter(f => !f.suppression).length})`,
    h('div', { class: 'toolbar' }, severity, h('label', null, showSuppressed, ' Show dismissed and expected'),
      h('span', { class: 'muted' }, report.analysisMs != null ? `Analyzed ${fmtNum(report.analyzedEvents)} events in ${fmtMs(report.analysisMs)}` : '')),
    list));

  const errors = Object.entries(report.ruleErrors || {});
  if (errors.length) {
    root.appendChild(panel('Rule errors', h('ul', { class: 'compact' }, errors.map(([id, message]) => h('li', null, `${id}: ${message}`)))));
  }

  const rules = report.rules?.length ? report.rules : (ctx.state.info?.rules || []);
  if (rules.length) {
    root.appendChild(panel('Rules', h('details', null, h('summary', null, `${rules.length} rules evaluated`), sortableTable({
      label: 'Rules',
      rows: rules,
      initialSort: { key: 'id', dir: 1 },
      columns: [
        { key: 'id', title: 'Id', render: r => h('span', { class: 'mono' }, `${r.id} v${r.version}`) },
        { key: 'title', title: 'Title' },
        { key: 'category', title: 'Category' },
        { key: 'description', title: 'Description' },
      ],
    }))));
  }

  return root;
}

function findingCard(ctx, f, open) {
  const card = h('details', { class: `finding${f.suppression ? ' suppressed' : ''}`, open: open || null });
  card.appendChild(h('summary', null,
    h('span', { class: `badge sev-${f.severity}` }, f.severity),
    h('span', null, h('strong', null, f.title), h('span', { class: 'muted' }, ` · ${f.ruleId} · ${f.category}`)),
    h('span', { class: 'badge', title: 'How strongly the evidence supports the explanation' }, `${f.confidence} confidence`)));

  const actions = h('div', { class: 'toolbar' });
  // Suppressions apply to every user, so only callers with access to all circuits may change them.
  const canSuppress = ctx.state.info?.canViewAllCircuits !== false;
  if (canSuppress && !ctx.state.source?.readOnly && !ctx.state.session?.fileImported) {
    if (f.suppression) {
      actions.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => ctx.act(s => s.unsuppress(f.fingerprint), 'Suppression removed') }, `Restore (${f.suppression.kind})`));
    } else {
      actions.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => ctx.act(s => s.suppress(f.fingerprint, 'Dismissed', null), 'Finding dismissed') }, 'Dismiss'));
      actions.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => ctx.act(s => s.suppress(f.fingerprint, 'Expected', null), 'Marked as expected') }, 'Mark as expected'));
    }
  }

  if (f.relatedEventIds?.length) {
    actions.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => ctx.navigate('timeline', { eventId: f.relatedEventIds[0] }) }, 'Show in timeline'));
  }

  // Findings can come from imported trace files or other untrusted sources; only web links are rendered.
  const documentationUrl = safeWebUrl(f.documentationUrl);
  if (documentationUrl) {
    actions.appendChild(h('a', { href: documentationUrl, target: '_blank', rel: 'noopener noreferrer' }, 'Rule documentation'));
  }

  card.appendChild(h('div', { class: 'body' },
    h('p', null, f.summary),
    h('div', null, h('h3', null, 'Why it matters'), h('p', null, f.explanation)),
    h('div', null, h('h3', null, 'Suggested fix'), h('p', null, f.recommendation)),
    f.codeHint ? h('p', { class: 'muted' }, 'Where to look: ', h('span', { class: 'mono' }, f.codeHint)) : null,
    h('div', null, h('h3', null, 'Evidence'), h('table', null, h('tbody', null, (f.evidence || []).map(e => h('tr', null, h('td', null, e.label), h('td', null, e.value), h('td', null, qualityBadge(e.quality))))))),
    f.impact ? h('p', { class: 'muted' }, `Impact: ${f.impact.totalMs != null ? fmtMs(f.impact.totalMs) + ' · ' : ''}${fmtNum(f.impact.occurrences, 1)} occurrence(s). Method: ${f.impact.method} `, qualityBadge(f.impact.quality)) : null,
    f.caveats?.length ? h('div', null, h('h3', null, 'May not apply when'), h('ul', { class: 'compact' }, f.caveats.map(c => h('li', null, c)))) : null,
    f.windowStart != null ? h('p', { class: 'muted' }, `Measurement window: ${fmtMs(ctx.toMs(f.windowStart))} – ${fmtMs(ctx.toMs(f.windowEnd))}`) : null,
    actions));
  return card;
}

// ----------------------------------------------------------------------------------------------- Sessions

export function sessions(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Sessions'));
  const list = ctx.state.info?.sessions || [];
  const source = ctx.state.source;
  const readOnly = !source || source.readOnly;

  const toolbar = h('div', { class: 'toolbar' },
    !readOnly ? h('button', { type: 'button', class: 'btn primary', onclick: () => ctx.act(s => s.start(prompt('Session name', `Session ${new Date().toLocaleTimeString()}`) || undefined), 'Session started') }, 'New session') : null,
    h('button', { type: 'button', class: 'btn', onclick: () => ctx.importFile() }, 'Import trace…'));

  root.appendChild(panel('Sessions', toolbar, list.length ? sortableTable({
    label: 'Sessions',
    rows: list,
    initialSort: { key: 'createdAt', dir: -1 },
    columns: [
      { key: 'name', title: 'Name', render: s => h('span', null, s.name, s.isImported ? h('span', { class: 'badge imported' }, 'imported') : null, s.id === ctx.state.sessionId ? h('span', { class: 'badge live' }, 'selected') : null) },
      { key: 'state', title: 'State', render: s => h('span', { class: `state-${s.state}` }, s.state) },
      { key: 'createdAt', title: 'Created', render: s => (s.createdAt ? new Date(s.createdAt).toLocaleString() : '—') },
      { key: 'durationMs', title: 'Duration', num: true, render: s => fmtMs(s.durationMs) },
      { key: 'storedEvents', title: 'Events', num: true, render: s => fmtNum(s.storedEvents) },
      { key: 'storedBytes', title: 'Memory', num: true, render: s => (s.storedBytes ? fmtBytes(s.storedBytes) : '—') },
      { key: 'evictedEvents', title: 'Evicted', num: true, render: s => fmtNum(s.evictedEvents) },
      { key: 'actions', title: 'Actions', render: s => sessionActions(ctx, s, readOnly) },
    ],
  }) : emptyState('No sessions', readOnly ? 'Import a trace file to analyze it.' : 'Start a session to begin recording.')));

  root.appendChild(panel('Compare sessions', compareView(ctx, list)));
  const drop = h('div', { class: 'dropzone', tabindex: '0', role: 'button', 'aria-label': 'Drop a trace file here or press Enter to choose one' }, 'Drop a .bptrace or .json trace here to open it locally (read-only, never uploaded).');
  drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('over'); });
  drop.addEventListener('dragleave', () => drop.classList.remove('over'));
  drop.addEventListener('drop', e => {
    e.preventDefault();
    drop.classList.remove('over');
    const file = e.dataTransfer.files[0];
    if (file) {
      ctx.openLocalFile(file);
    }
  });
  drop.addEventListener('keydown', e => { if (e.key === 'Enter') { ctx.importFile(true); } });
  root.appendChild(panel('Open a trace file', drop));
  return root;
}

function sessionActions(ctx, s, readOnly) {
  const box = h('div', { class: 'toolbar' });
  const add = (label, fn, message) => box.appendChild(h('button', { type: 'button', class: 'btn small', onclick: e => { e.stopPropagation(); ctx.act(fn, message); } }, label));
  box.appendChild(h('button', { type: 'button', class: 'btn small', onclick: e => { e.stopPropagation(); ctx.selectSession(s.id); } }, 'Open'));
  if (!readOnly && !s.isImported) {
    if (s.state === 'Recording') {
      add('Pause', src => src.control(s.id, 'pause'), 'Session paused');
      add('Bookmark', src => src.control(s.id, 'bookmark', prompt('Bookmark name', 'Bookmark') || 'Bookmark'), 'Bookmark added');
    }

    if (s.state === 'Paused') {
      add('Resume', src => src.control(s.id, 'resume'), 'Session resumed');
    }

    if (s.state === 'Recording' || s.state === 'Paused') {
      add('Stop', src => src.control(s.id, 'stop'), 'Session stopped');
    }

    add('Clear', src => src.control(s.id, 'clear'), 'Session cleared');
  }

  if (!readOnly || s.fileImported) {
    add('Remove', src => (confirm(`Remove session "${s.name}"? Its data will be discarded.`) ? src.remove(s.id) : null), 'Session removed');
  }

  for (const format of s.fileImported ? ['json'] : ['native', 'json', 'chrome']) {
    box.appendChild(h('button', { type: 'button', class: 'btn small', title: format === 'chrome' ? 'Chrome Trace Event format (Perfetto, chrome://tracing)' : format === 'native' ? 'Compressed native trace (.bptrace)' : 'Uncompressed native trace (JSON)', onclick: e => { e.stopPropagation(); ctx.exportSession(s.id, format); } }, `Export ${format}`));
  }

  return box;
}

function compareView(ctx, list) {
  const host = h('div');
  if (list.length < 2) {
    host.appendChild(emptyState('Need two sessions', 'Record or import another session to compare before/after a change.'));
    return host;
  }

  const option = s => h('option', { value: s.id }, `${s.name} (${s.state})`);
  const a = h('select', { 'aria-label': 'Baseline session' }, list.map(option));
  const b = h('select', { 'aria-label': 'Comparison session' }, list.map(option));
  b.value = ctx.state.sessionId || list[list.length - 1].id;
  a.value = list.find(s => s.id !== b.value)?.id || list[0].id;
  const result = h('div');
  const run = async () => {
    clear(result).appendChild(h('div', { class: 'loading' }, 'Comparing…'));
    try {
      const [sa, sb] = await Promise.all([ctx.summaryFor(a.value), ctx.summaryFor(b.value)]);
      clear(result).appendChild(compareTable(sa, sb));
    } catch (error) {
      clear(result).appendChild(emptyState('Comparison failed', error.message));
    }
  };

  host.append(h('div', { class: 'toolbar' }, h('label', null, 'Baseline ', a), h('label', null, 'Compare with ', b), h('button', { type: 'button', class: 'btn', onclick: run }, 'Compare')), result);
  return host;
}

function compareTable(a, b) {
  if (!a || !b) {
    return emptyState('Summary unavailable', 'One of the sessions has no summary.');
  }

  const sum = (s, f) => (s || []).reduce((n, x) => n + f(x), 0);
  const metrics = [
    ['Render batches', x => x.renderBatches.durations.count, fmtNum],
    ['Render batch p95', x => x.renderBatches.durations.p95Ms, fmtMs],
    ['Components updated', x => x.renderBatches.totalUpdatedComponents, fmtNum],
    ['Component renders (instrumented)', x => sum(x.components, c => c.renders.count), fmtNum],
    ['Render time (instrumented)', x => sum(x.components, c => c.renders.totalMs), fmtMs],
    ['Parameter updates', x => sum(x.components, c => c.parametersSets.count), fmtNum],
    ['Event handlers', x => sum(x.handlers, c => c.durations.count), fmtNum],
    ['Handler time', x => sum(x.handlers, c => c.durations.totalMs), fmtMs],
    ['HTTP requests', x => sum(x.endpoints, c => c.durations.count), fmtNum],
    ['HTTP time', x => sum(x.endpoints, c => c.durations.totalMs), fmtMs],
    ['Interop calls', x => sum(x.interop, c => c.durations.count), fmtNum],
    ['Allocated', x => x.runtime.allocatedBytes, fmtBytes],
    ['GC pause', x => (x.runtime.gcPauseUnavailable ? null : x.runtime.gcPauseMs), fmtMs],
  ];

  const row = ([label, get, format]) => {
    const ra = get(a);
    const rb = get(b);
    if (ra === null || rb === null) {
      // Not measured in one of the sessions (for example GC pause time in WebAssembly): no meaningful change.
      return h('tr', null, h('td', null, label), h('td', { class: 'num' }, ra === null ? 'unavailable' : format(ra || 0)),
        h('td', { class: 'num' }, rb === null ? 'unavailable' : format(rb || 0)), h('td', { class: 'num' }, '—'));
    }

    const va = ra || 0;
    const vb = rb || 0;
    const delta = va ? (vb - va) / va : (vb ? 1 : 0);
    return h('tr', null, h('td', null, label), h('td', { class: 'num' }, format(va)), h('td', { class: 'num' }, format(vb)),
      h('td', { class: `num ${delta > 0.05 ? 'compare-up' : delta < -0.05 ? 'compare-down' : ''}` }, va || vb ? `${delta > 0 ? '+' : ''}${(delta * 100).toFixed(0)}%` : '—'));
  };

  const components = new Map();
  for (const c of a.components) {
    components.set(c.componentType, { name: c.componentType, a: c.renders.count || c.parametersSets.count, b: 0 });
  }

  for (const c of b.components) {
    const entry = components.get(c.componentType) || { name: c.componentType, a: 0, b: 0 };
    entry.b = c.renders.count || c.parametersSets.count;
    components.set(c.componentType, entry);
  }

  const changes = [...components.values()].map(c => ({ ...c, delta: c.b - c.a })).sort((x, y) => Math.abs(y.delta) - Math.abs(x.delta)).slice(0, 10);
  return h('div', { class: 'grid cols-2' },
    h('table', { 'aria-label': 'Metric comparison' },
      h('thead', null, h('tr', null, h('th', null, 'Metric'), h('th', { class: 'num' }, 'Baseline'), h('th', { class: 'num' }, 'Compared'), h('th', { class: 'num' }, 'Change'))),
      h('tbody', null, metrics.map(row))),
    h('table', { 'aria-label': 'Largest component changes' },
      h('thead', null, h('tr', null, h('th', null, 'Component (renders or param updates)'), h('th', { class: 'num' }, 'Baseline'), h('th', { class: 'num' }, 'Compared'))),
      h('tbody', null, changes.map(c => h('tr', null, h('td', { title: c.name }, shortType(c.name)), h('td', { class: 'num' }, fmtNum(c.a)), h('td', { class: `num ${c.delta > 0 ? 'compare-up' : c.delta < 0 ? 'compare-down' : ''}` }, fmtNum(c.b)))))));
}

// ----------------------------------------------------------------------------------------------- Health

export function health(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Profiler health'));
  const info = ctx.state.info;
  const healthInfo = info?.health;
  const fileMeta = ctx.state.fileMeta;
  if (!healthInfo && !fileMeta) {
    root.appendChild(emptyState('No health data', 'Health is reported by live profilers.'));
    return root;
  }

  if (healthInfo && healthInfo.emitted != null && info.canViewAllCircuits !== false) {
    root.appendChild(h('div', { class: 'tiles' },
      tile('Profile', healthInfo.profile),
      tile('Events submitted', fmtNum(healthInfo.emitted)),
      tile('Processed', fmtNum(healthInfo.processed)),
      tile('Dropped (queue full)', fmtNum(healthInfo.droppedQueueFull), healthInfo.droppedQueueFull ? 'increase Pipeline.QueueCapacity or sample' : 'none'),
      tile('Sampled out', fmtNum(healthInfo.sampledOut)),
      tile('Filtered / merged', fmtNum(healthInfo.filtered)),
      tile('Internal errors', fmtNum(healthInfo.internalErrors)),
      tile('Queue', `${fmtNum(healthInfo.queueLength)} / ${fmtNum(healthInfo.queueCapacity)}`),
      tile('Emit cost', `${fmtNum(healthInfo.emitMicrosecondsPerEvent, 2)} µs`, 'on the instrumented thread (sampled)'),
      tile('Processing cost', `${fmtNum(healthInfo.processingMicrosecondsPerEvent, 2)} µs`, `total ${fmtMs(healthInfo.processingMs)} (background)`),
    ));
  }

  const collectors = healthInfo?.collectors || fileMeta?.collectors || [];
  root.appendChild(panel('Collectors', collectors.length ? sortableTable({
    label: 'Collectors',
    rows: collectors.map(c => ({ ...c, name: c.descriptor.name })),
    initialSort: { key: 'name', dir: 1 },
    columns: [
      { key: 'name', title: 'Collector', render: c => h('span', { title: c.descriptor.description }, c.descriptor.name, c.descriptor.optIn ? h('span', { class: 'badge' }, 'opt-in') : null) },
      { key: 'state', title: 'State', render: c => h('span', { class: `state-${c.state}` }, c.state) },
      { key: 'eventsEmitted', title: 'Events', num: true, render: c => fmtNum(c.eventsEmitted) },
      { key: 'errors', title: 'Errors', num: true },
      { key: 'capability', title: 'Capability', sort: c => c.descriptor.capability, render: c => c.descriptor.capability },
      { key: 'overhead', title: 'Overhead', sort: c => c.descriptor.overhead, render: c => c.descriptor.overhead },
      { key: 'details', title: 'Notes', render: c => h('details', null, h('summary', null, c.message || c.lastError || 'details'),
        h('dl', { class: 'kv' },
          h('dt', null, 'Observes'), h('dd', null, c.descriptor.description),
          h('dt', null, 'Mechanism'), h('dd', null, c.descriptor.mechanism),
          h('dt', null, 'Compatibility'), h('dd', null, c.descriptor.compatibility || '—'),
          h('dt', null, 'Degradation'), h('dd', null, c.descriptor.degradation || '—'),
          c.lastError ? [h('dt', null, 'Last error'), h('dd', null, c.lastError)] : null)) },
    ],
  }) : emptyState('No collector information')));
  return root;
}

// ----------------------------------------------------------------------------------------------- Event details

export function eventDetails(ctx, e, related) {
  const skip = new Set(['type', '__s', '__e', '__row', '__dup']);
  const rows = Object.entries(e).filter(([k, v]) => !skip.has(k) && v !== null && v !== undefined && v !== '');
  const format = (key, value) => {
    if (key === 'timestamp') {
      return `${fmtMs(ctx.toMs(value))} (${ctx.wallTime(value)})`;
    }

    if (key === 'duration' || key === 'synchronousDuration' || key === 'lifetimeTicks' || key === 'gcPauseTicks' || key === 'cpuTicks') {
      return fmtMs(ctx.ticksToMs(value));
    }

    if (/Bytes$/.test(key) || key === 'transferSize' || key === 'decodedBodySize') {
      return fmtBytes(value);
    }

    return Array.isArray(value) ? value.join(', ') : String(value);
  };

  const root = h('div', null,
    h('p', null, h('strong', null, describeShort(e))),
    h('dl', { class: 'kv' }, h('dt', null, 'kind'), h('dd', null, e.type), rows.map(([k, v]) => [h('dt', null, k), h('dd', { class: 'mono' }, format(k, v))])));

  if (e.correlation) {
    root.appendChild(h('p', { class: 'muted' }, correlationText(e.correlation)));
  }

  if (related) {
    root.appendChild(h('h3', null, `Operation #${e.operationId}: ${related.length} related event(s)`));
    root.appendChild(related.length ? eventList(ctx, related.sort((a, b) => a.timestamp - b.timestamp), 260, { compact: true }) : emptyState('No retained events for this operation'));
  }

  return root;
}

function correlationText(kind) {
  switch (kind) {
    case 'Self': return 'This event is the operation itself.';
    case 'Explicit': return 'Linked explicitly by instrumentation.';
    case 'ExecutionContext': return 'Linked because it ran in the operation\'s execution context (strong evidence of causation).';
    case 'Dispatcher': return 'Linked because it ran on the same renderer dispatcher right after the operation (good evidence).';
    case 'Temporal': return 'Linked by time overlap only (weak evidence; not proof of causation).';
    default: return 'Not correlated with an operation.';
  }
}
