// Responsiveness view: what the user felt on the browser's main thread. Interactions (Event Timing), long animation
// frames with script attribution (which script, invoked how, and how much forced layout it caused), and frustration
// signals (rage and dead clicks).
import { h, clear, fmtMs, fmtNum, sortableTable, emptyState, debounce } from './util.js';
import { inRange } from './views.js';

const FRAMEWORK_FILE = /(^|\/)(blazor\.[^/]*\.js|dotnet\.[^/]*\.js)(\?|#|$)|\/_framework\//i;

function fileOf(url) {
  if (!url) {
    return '';
  }

  const path = url.split(/[?#]/)[0];
  return path.slice(path.lastIndexOf('/') + 1) || path;
}

export function scriptLabel(s) {
  const file = fileOf(s.sourceUrl);
  const fn = s.sourceFunctionName || '';
  if (fn && file) {
    return `${fn} (${file})`;
  }

  if (fn) {
    return fn;
  }

  if (file) {
    return `${s.invoker || 'script'} (${file})`;
  }

  return s.invoker || '(unknown script)';
}

export function isFrameworkScript(s) {
  return FRAMEWORK_FILE.test(s.sourceUrl || '') || FRAMEWORK_FILE.test(s.invoker || '');
}

// Aggregates the scripts of long animation frames by label, longest total first.
export function aggregateScripts(frames) {
  const groups = new Map();
  for (const f of frames) {
    for (const s of f.scripts || []) {
      const label = scriptLabel(s);
      let g = groups.get(label);
      if (!g) {
        g = { label, frames: new Set(), total: 0, forced: 0, pause: 0, max: 0, invokerType: s.invokerType, sourceUrl: s.sourceUrl, invoker: s.invoker, framework: isFrameworkScript(s), examples: [] };
        groups.set(label, g);
      }

      g.frames.add(f.id);
      g.total += s.durationMs || 0;
      g.forced += s.forcedStyleAndLayoutMs || 0;
      g.pause += s.pauseMs || 0;
      if ((s.durationMs || 0) > g.max) {
        g.max = s.durationMs || 0;
        g.invokerType = s.invokerType;
        g.invoker = s.invoker;
      }

      if (g.examples.length < 50) {
        g.examples.push(f);
      }
    }
  }

  return [...groups.values()].map(g => ({ ...g, frameCount: g.frames.size })).sort((a, b) => b.total - a.total);
}

function tile(label, value, sub, title) {
  return h('div', { class: 'tile', title: title || null }, h('div', { class: 'label' }, label), h('div', { class: 'value' }, value), sub ? h('div', { class: 'sub' }, sub) : null);
}

function panel(title, ...content) {
  return h('section', { class: 'panel' }, h('h2', null, title), ...content);
}

export function responsiveness(ctx) {
  const root = h('div');
  root.appendChild(h('h1', null, 'Responsiveness'));
  const interactions = inRange(ctx, ctx.state.events.filter(e => e.type === 'browserInteraction'));
  const frames = inRange(ctx, ctx.state.events.filter(e => e.type === 'browserLongTask'));
  const signals = inRange(ctx, ctx.state.events.filter(e => e.type === 'browserFrustration'));
  if (!interactions.length && !frames.length && !signals.length) {
    root.appendChild(emptyState('No browser responsiveness data',
      'Render <BlazorProfilerClient /> in an interactive component. Interactions (Event Timing), long animation frames with script attribution, and rage or dead clicks appear here. Long animation frame attribution requires a Chromium-based browser.'));
    return root;
  }

  const forcedTotal = frames.reduce((sum, f) => sum + (f.forcedStyleAndLayoutMs || 0), 0);
  const blockingTotal = frames.reduce((sum, f) => sum + (f.blockingMs || 0), 0);
  const durations = interactions.map(i => ctx.ticksToMs(i.duration)).sort((a, b) => a - b);
  const p95 = durations.length ? durations[Math.min(durations.length - 1, Math.ceil(durations.length * 0.95) - 1)] : 0;
  const rage = signals.filter(s => s.signal === 'RageClick');
  const dead = signals.filter(s => s.signal === 'DeadClick');
  root.appendChild(h('div', { class: 'tiles' },
    tile('Interactions', fmtNum(interactions.length), durations.length ? `p95 ${fmtMs(p95)} · max ${fmtMs(durations[durations.length - 1])}` : 'none above the threshold'),
    tile('Long animation frames', fmtNum(frames.length), `${fmtMs(blockingTotal)} blocking`),
    tile('Forced layout', fmtMs(forcedTotal), 'synchronous style and layout caused by scripts', 'Time scripts forced the browser to compute style and layout synchronously (reading geometry after changing the DOM).'),
    tile('Rage clicks', fmtNum(rage.length), rage.length ? `up to ${fmtNum(Math.max(...rage.map(r => r.clickCount || 0)))} clicks` : null),
    tile('Dead clicks', fmtNum(dead.length), 'clicks that changed nothing'),
  ));

  if (frames.length) {
    const scripts = aggregateScripts(frames);
    if (scripts.length) {
      root.appendChild(panel('Scripts in long animation frames', sortableTable({
        label: 'Long animation frame scripts',
        rows: scripts,
        initialSort: { key: 'total', dir: -1 },
        maxHeight: 420,
        columns: [
          { key: 'label', title: 'Script', render: s => h('span', { class: 'mono', title: s.sourceUrl || s.invoker || '' }, s.framework ? h('span', { class: 'badge', title: 'Blazor framework or .NET WebAssembly runtime: .NET work (rendering, handlers) shows up here' }, '.NET') : null, ' ', s.label) },
          { key: 'invokerType', title: 'Invoked by', render: s => h('span', { title: s.invoker || '' }, s.invokerType || '—') },
          { key: 'frameCount', title: 'Frames', num: true, render: s => fmtNum(s.frameCount) },
          { key: 'total', title: 'Total', num: true, render: s => fmtMs(s.total) },
          { key: 'max', title: 'Longest', num: true, render: s => fmtMs(s.max) },
          { key: 'forced', title: 'Forced layout', num: true, render: s => (s.forced >= 1 ? h('span', { class: 'dup', title: 'Synchronous style and layout forced by this script' }, fmtMs(s.forced)) : fmtMs(s.forced)) },
          { key: 'pause', title: 'Paused', num: true, render: s => fmtMs(s.pause) },
        ],
        onSelect: s => ctx.openDrawer(`Script: ${s.label}`, h('div', null,
          h('p', null, `${s.invokerType || 'script'} · invoker ${s.invoker || 'unknown'}`),
          s.sourceUrl ? h('p', { class: 'mono' }, s.sourceUrl) : null,
          s.framework ? h('p', { class: 'muted' }, 'This is Blazor or the .NET WebAssembly runtime. The .NET work it ran (rendering, event handlers, JSON) is in the timeline at the same time.') : null,
          h('h3', null, 'Frames'),
          h('ul', null, s.examples.map(f => h('li', null, h('a', { href: '#', onclick: ev => { ev.preventDefault(); ctx.showEvent(f); } }, `${fmtMs(ctx.toMs(f.timestamp))}: ${fmtMs(ctx.ticksToMs(f.duration))} frame`)))))),
      }), h('p', { class: 'muted' }, 'Long Animation Frames API script attribution (Chromium). Scripts shorter than 5 ms are not attributed by the browser. Blazor dispatches events through its own listeners, so .NET handlers and rendering appear as Blazor framework scripts.')));
    }

    root.appendChild(panel('Long animation frames', sortableTable({
      label: 'Long animation frames',
      rows: frames,
      initialSort: { key: 'duration', dir: -1 },
      maxHeight: 420,
      columns: [
        { key: 'timestamp', title: 'Start', num: true, render: f => fmtMs(ctx.toMs(f.timestamp)) },
        { key: 'duration', title: 'Duration', num: true, render: f => fmtMs(ctx.ticksToMs(f.duration)) },
        { key: 'blockingMs', title: 'Blocking', num: true, render: f => fmtMs(f.blockingMs) },
        { key: 'renderMs', title: 'Rendering', num: true, render: f => (f.renderMs ? fmtMs(f.renderMs) : '—') },
        { key: 'styleAndLayoutMs', title: 'Style + layout', num: true, render: f => (f.styleAndLayoutMs ? fmtMs(f.styleAndLayoutMs) : '—') },
        { key: 'forcedStyleAndLayoutMs', title: 'Forced layout', num: true, render: f => (f.forcedStyleAndLayoutMs >= 1 ? h('span', { class: 'dup' }, fmtMs(f.forcedStyleAndLayoutMs)) : '—') },
        { key: 'script', title: 'Longest script', sort: f => (f.scripts && f.scripts[0] ? scriptLabel(f.scripts[0]) : f.script || ''), render: f => h('span', { class: 'mono' }, f.scripts && f.scripts[0] ? scriptLabel(f.scripts[0]) : (f.script || '—')) },
      ],
      onSelect: f => ctx.showEvent(f),
    })));
  }

  if (interactions.length) {
    root.appendChild(panel('Interactions', sortableTable({
      label: 'Browser interactions',
      rows: interactions,
      initialSort: { key: 'duration', dir: -1 },
      maxHeight: 420,
      columns: [
        { key: 'timestamp', title: 'Start', num: true, render: i => fmtMs(ctx.toMs(i.timestamp)) },
        { key: 'eventType', title: 'Event', render: i => `${i.eventType || '?'}${i.targetTag ? ` on <${i.targetTag}>` : ''}` },
        { key: 'duration', title: 'Duration', num: true, render: i => fmtMs(ctx.ticksToMs(i.duration)) },
        { key: 'inputDelayMs', title: 'Input delay', num: true, render: i => fmtMs(i.inputDelayMs) },
        { key: 'processingMs', title: 'Processing', num: true, render: i => fmtMs(i.processingMs) },
        { key: 'presentationDelayMs', title: 'Presentation', num: true, render: i => fmtMs(i.presentationDelayMs) },
        { key: 'contentPaintMs', title: 'Content painted', num: true, sort: i => i.contentPaintMs ?? -1, render: i => (i.contentPaintMs == null ? '—' : h('span', { class: i.contentPaintMs >= 200 && i.contentPaintMs >= 2 * ctx.ticksToMs(i.duration) ? 'dup' : null, title: 'Input to the paint of the content the interaction changed (interaction contentful paint, Chromium; not reported for Blazor Server)' }, fmtMs(i.contentPaintMs))) },
      ],
      onSelect: i => ctx.showEvent(i),
    }), h('p', { class: 'muted' }, 'Input delay: the main thread was busy when the input arrived. Processing: event handlers (in WebAssembly, .NET handlers run here). Presentation: rendering and painting the result. Content painted: when the content the interaction changed appeared, including results rendered after awaited work (Chromium, WebAssembly). On Blazor Server the handler runs on the server after the browser has painted, so slow server work shows up as slow render round trips instead.')));
  }

  if (signals.length) {
    const byTarget = new Map();
    for (const s of signals) {
      const key = s.target || s.targetTag || '(element)';
      const g = byTarget.get(key) || { target: key, rage: 0, dead: 0, clicks: 0, last: 0, events: [] };
      if (s.signal === 'RageClick') {
        g.rage++;
      } else {
        g.dead++;
      }

      g.clicks += s.clickCount || 1;
      g.last = Math.max(g.last, s.timestamp);
      g.events.push(s);
      byTarget.set(key, g);
    }

    const filter = h('input', { type: 'search', placeholder: 'Element contains…', 'aria-label': 'Filter frustration signals' });
    const host = h('div');
    const render = () => {
      const term = filter.value.toLowerCase();
      clear(host).appendChild(sortableTable({
        label: 'Frustration signals by element',
        rows: [...byTarget.values()].filter(g => !term || g.target.toLowerCase().includes(term)),
        initialSort: { key: 'rage', dir: -1 },
        columns: [
          { key: 'target', title: 'Element', render: g => h('span', { class: 'mono' }, g.target) },
          { key: 'rage', title: 'Rage clicks', num: true, render: g => (g.rage ? h('span', { class: 'dup' }, fmtNum(g.rage)) : '0') },
          { key: 'dead', title: 'Dead clicks', num: true, render: g => fmtNum(g.dead) },
          { key: 'clicks', title: 'Clicks', num: true, render: g => fmtNum(g.clicks) },
        ],
        onSelect: g => ctx.showEvent(g.events[0]),
      }));
    };
    filter.addEventListener('input', debounce(render, 120));
    render();
    root.appendChild(panel('Rage and dead clicks', h('div', { class: 'toolbar' }, filter), host,
      h('p', { class: 'muted' }, 'Rage click: 3 or more clicks within 1 s on the same spot. Dead click: a click on a button or link after which the page did not change for 1 s. Only the element’s tag, id, and classes are recorded. Exclude elements with Collectors.FrustrationIgnoreSelector.')));
  }

  return root;
}
