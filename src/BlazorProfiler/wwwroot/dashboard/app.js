// Blazor Profiler dashboard: source selection, polling, routing, and shared view context.
// The dashboard is plain JavaScript (no Blazor), so it never adds render batches or interop calls to the profiled app.
import { settings, loadSettings, saveSettings, h, clear, downloadBlob } from './util.js';
import { ApiSource, BridgeSource, FileSource } from './sources.js';
import * as views from './views.js';

const MAX_EVENTS = 150_000;
const PAGE_SIZE = 5_000;
const AUTO_RENDER_VIEWS = new Set(['overview', 'runtime', 'health']);
const VIEWS = {
  overview: views.overview,
  components: views.components,
  timeline: views.timeline,
  network: views.network,
  interop: views.interop,
  runtime: views.runtime,
  circuits: views.circuits,
  diagnostics: views.diagnostics,
  sessions: views.sessions,
  health: views.health,
};

const state = {
  source: null,
  liveSource: null,
  fileSource: new FileSource(),
  info: null,
  sessionId: null,
  session: null,
  events: [],
  cursor: 0,
  summary: null,
  report: null,
  live: true,
  view: 'overview',
  params: null,
  error: null,
  fileMeta: null,
  liveSessions: [],
  tick: 0,
  pendingRefresh: false,
  // Incremented whenever per-session data is reset or invalidated. A poll that started under an older generation
  // discards its results instead of writing another session's (or outdated) data into the current state.
  generation: 0,
};

let pollTimer = 0;
let pollPromise = null;
let pollAgain = false;
let timelineRef = null;
let lastFocus = null;

const $ = id => document.getElementById(id);

// ----------------------------------------------------------------------------------------------- Context for views

const ctx = {
  state,
  toMs: ts => {
    const s = state.session;
    return s ? ((ts - (s.startTimestamp || 0)) / s.clockFrequency) * 1000 : 0;
  },
  ticksToMs: ticks => (state.session ? (ticks / state.session.clockFrequency) * 1000 : 0),
  wallTime: ts => {
    const s = state.session;
    if (!s || !s.clockAnchorTime) {
      return '';
    }

    const ms = Date.parse(s.clockAnchorTime) + ((ts - s.clockAnchorTimestamp) / s.clockFrequency) * 1000;
    return new Date(ms).toISOString().replace('T', ' ').replace('Z', ' UTC');
  },
  maxMs: () => {
    let max = 0;
    for (let i = Math.max(0, state.events.length - 5000); i < state.events.length; i++) {
      const e = state.events[i];
      max = Math.max(max, ctx.toMs(e.timestamp + (e.duration || 0)));
    }

    return max;
  },
  navigate(view, params) {
    state.params = params || null;
    if (location.hash !== `#${view}`) {
      location.hash = view;
    } else {
      render();
    }
  },
  openDrawer,
  showEvent,
  registerTimeline(tl) {
    timelineRef?.destroy();
    timelineRef = tl;
  },
  act,
  importFile,
  openLocalFile,
  exportSession,
  selectSession,
  async summaryFor(id) {
    const source = id.startsWith('file-') ? state.fileSource : state.liveSource;
    return source.summary(id);
  },
};

// ----------------------------------------------------------------------------------------------- Startup

async function init() {
  loadSettings();
  applyTheme();
  bindSettings();
  bindChrome();

  const query = new URLSearchParams(location.search);
  const mode = query.get('source');
  const api = mode === 'api' && query.get('api') ? sameOriginPath(query.get('api')) : null;
  if (api) {
    useLiveSource(new ApiSource(api));
  } else {
    if (mode === 'api') {
      // Never load profiling data from another origin: a crafted link could otherwise make this page (served by the app)
      // display attacker-controlled data.
      state.error = 'The api parameter must be a path on this site; other origins are not allowed.';
      showBanner(state.error, 'error');
    }

    await discoverBridge(mode === 'bridge');
  }

  route();
  window.addEventListener('hashchange', route);
}

/** Returns the path of a same-origin URL (absolute or relative), or null for any other origin. */
function sameOriginPath(value) {
  try {
    const url = new URL(value, location.href);
    return url.origin === location.origin ? url.pathname : null;
  } catch {
    return null;
  }
}

async function discoverBridge(expected) {
  const picker = clear($('source-picker'));
  picker.appendChild(h('span', null, 'Looking for running apps…'));
  const clients = await BridgeSource.discover(expected ? 900 : 500);
  clear(picker);
  if (clients.length) {
    const select = h('select', { 'aria-label': 'Connected application' }, clients.map(c => h('option', { value: c.clientId }, `${c.title || 'App'} — ${c.path} (${c.source})`)));
    select.addEventListener('change', () => useLiveSource(new BridgeSource(clients.find(c => c.clientId === select.value))));
    picker.append(h('span', null, 'App:'), select, h('button', { type: 'button', class: 'btn small', onclick: () => discoverBridge(true) }, 'Rescan'));
    useLiveSource(new BridgeSource(clients[0]));
  } else {
    picker.append(
      h('span', null, expected ? 'No running app found. Keep the app tab open and make sure <BlazorProfilerClient /> is rendered.' : 'Offline viewer.'),
      h('button', { type: 'button', class: 'btn small', onclick: () => discoverBridge(true) }, 'Rescan'),
      h('button', { type: 'button', class: 'btn small', onclick: () => importFile(true) }, 'Open trace…'));
    state.source = null;
    render();
  }
}

function useLiveSource(source) {
  if (state.liveSource && state.liveSource !== source && state.liveSource.close) {
    state.liveSource.close();
  }

  state.liveSource = source;
  state.source = source;
  if (source.kind === 'api') {
    const picker = clear($('source-picker'));
    picker.append(h('span', null, 'Server:'), h('span', { class: 'mono' }, source.base));
  }

  resetSessionData();
  state.sessionId = null;
  schedulePoll(0);
}

// ----------------------------------------------------------------------------------------------- Polling

function schedulePoll(delay) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(poll, delay ?? (document.hidden ? 5000 : settings.refreshMs));
}

/**
 * Polls the current source. Only one poll runs at a time; a call made while one is in flight (for example from
 * selectSession or act) requests another pass and returns a promise that settles after that pass, so callers always
 * observe data loaded for the state they set up.
 */
function poll() {
  if (!state.source) {
    return Promise.resolve();
  }

  if (pollPromise) {
    pollAgain = true;
    return pollPromise;
  }

  pollPromise = (async () => {
    try {
      do {
        pollAgain = false;
        await pollOnce();
      } while (pollAgain && state.source);
    } finally {
      pollPromise = null;
      if (state.live && state.source && !state.source.readOnly) {
        schedulePoll();
      }
    }
  })();
  return pollPromise;
}

async function pollOnce() {
  const source = state.source;
  if (!source) {
    return;
  }

  let generation = state.generation;
  // True when the session selection, the source, or the session data changed while this pass was awaiting: its results
  // then belong to another state and must be discarded (a follow-up pass loads the current one).
  const stale = () => generation !== state.generation || source !== state.source;
  let changed = false;
  try {
    const info = await source.info();
    if (source !== state.source) {
      return;
    }

    state.info = info;
    state.error = null;
    if (source === state.liveSource) {
      state.liveSessions = info.sessions;
    }

    updateSessionSelect();

    if (!state.sessionId || !info.sessions.some(s => s.id === state.sessionId)) {
      const preferred = info.sessions.find(s => s.state === 'Recording') || info.sessions[info.sessions.length - 1];
      state.sessionId = preferred?.id || null;
      resetSessionData();
      changed = true;
      updateSessionSelect();
    }

    // The selection may have changed while info was loading; continue with the current one from here on.
    generation = state.generation;
    const sessionId = state.sessionId;
    state.session = info.sessions.find(s => s.id === sessionId) || null;
    if (state.session) {
      const loaded = await loadEvents(source, sessionId, stale);
      if (stale()) {
        return;
      }

      changed = loaded || changed;
      const summaryEvery = AUTO_RENDER_VIEWS.has(state.view) ? 1 : 2;
      if (!state.summary || state.tick % summaryEvery === 0 || changed) {
        const summary = await source.summary(sessionId);
        if (stale()) {
          return;
        }

        state.summary = summary;
      }

      const findingsEvery = state.view === 'diagnostics' ? 3 : 5;
      if (!state.report || state.tick % findingsEvery === 0) {
        const report = await source.findings(sessionId);
        if (stale()) {
          return;
        }

        changed = changed || JSON.stringify(report.findings?.map(f => f.fingerprint + f.severity + (f.suppression ? 1 : 0))) !== JSON.stringify(state.report?.findings?.map(f => f.fingerprint + f.severity + (f.suppression ? 1 : 0)));
        state.report = report;
      }
    }

    updateChrome();
    state.tick++;
    if (state.tick === 1 || AUTO_RENDER_VIEWS.has(state.view)) {
      render();
    } else if (changed) {
      if (state.view === 'timeline' && timelineRef) {
        timelineRef.setData(state.events.slice(), ctx.toMs);
      } else {
        showRefreshHint();
      }
    }
  } catch (error) {
    if (stale()) {
      return;
    }

    state.error = error.message;
    showBanner(error.message, 'error');
  }
}

async function loadEvents(source, sessionId, stale) {
  let changed = false;
  for (let page = 0; page < 6; page++) {
    const result = await source.events(sessionId, state.cursor, PAGE_SIZE);
    if (stale()) {
      return false;
    }

    if (result.reset || result.cursor < state.cursor) {
      // The session was cleared since our cursor was issued: drop what we have and continue from the new events.
      state.events = [];
      changed = true;
    } else if (result.gap && state.cursor > 0) {
      // Older events were evicted; keep what we have but note the gap.
      showBanner('Some events were evicted by retention limits before they could be loaded; the view may be incomplete.', 'warn');
    }

    if (result.events.length) {
      state.events.push(...result.events);
      changed = true;
    }

    state.cursor = result.cursor;
    if (!result.hasMore) {
      break;
    }
  }

  if (state.events.length > MAX_EVENTS) {
    state.events.splice(0, state.events.length - MAX_EVENTS);
  }

  return changed;
}

function resetSessionData() {
  state.generation++;
  state.events = [];
  state.cursor = 0;
  state.summary = null;
  state.report = null;
  state.fileMeta = null;
  state.tick = 0;
}

async function selectSession(id) {
  if (id.startsWith('file-')) {
    state.source = state.fileSource;
  } else if (state.liveSource) {
    state.source = state.liveSource;
  }

  state.sessionId = id;
  resetSessionData();
  state.fileMeta = id.startsWith('file-') ? state.fileSource.metadata(id) : null;
  await poll();
  render();
}

// ----------------------------------------------------------------------------------------------- Rendering

function route() {
  const view = (location.hash || '#overview').slice(1);
  state.view = VIEWS[view] ? view : 'overview';
  for (const link of document.querySelectorAll('.sidenav a')) {
    if (link.dataset.view === state.view) {
      link.setAttribute('aria-current', 'page');
    } else {
      link.removeAttribute('aria-current');
    }
  }

  render();
  $('main').focus({ preventScroll: true });
}

function render() {
  const host = clear($('view'));
  hideRefreshHint();
  if (state.view !== 'timeline' && timelineRef) {
    timelineRef.destroy();
    timelineRef = null;
  }

  if (!state.source) {
    host.appendChild(views.sessions(ctx));
    return;
  }

  if (!state.session && state.view !== 'sessions' && state.view !== 'health') {
    host.appendChild(h('div', { class: 'empty' }, h('strong', null, state.error ? 'Unable to load profiling data' : 'No session selected'),
      state.error || 'Start a session on the Sessions page, or import a trace.'));
    return;
  }

  try {
    host.appendChild(VIEWS[state.view](ctx, state.params));
  } catch (error) {
    host.appendChild(h('div', { class: 'banner error', role: 'alert' }, `This view failed to render: ${error.message}`));
    console.error(error);
  }

  state.params = null;
}

function updateSessionSelect() {
  const select = $('session-select');
  const live = state.liveSessions || [];
  const files = [...state.fileSource.docs.keys()].map(id => state.fileSource.stats(id, state.fileSource.docs.get(id)));
  const all = [...live, ...files];
  clear(select);
  for (const s of all) {
    select.appendChild(h('option', { value: s.id }, `${s.name} — ${s.state}`));
  }

  if (state.sessionId) {
    select.value = state.sessionId;
  }
}

function updateChrome() {
  const badge = $('mode-badge');
  const s = state.session;
  badge.className = 'badge';
  if (!s) {
    badge.textContent = '';
  } else if (s.isImported) {
    badge.textContent = 'imported trace';
    badge.classList.add('imported');
  } else {
    badge.textContent = s.state === 'Recording' ? 'live' : s.state.toLowerCase();
    if (s.state === 'Recording') {
      badge.classList.add('live');
    }
  }

  const count = (state.report?.findings || []).filter(f => !f.suppression).length;
  $('finding-count').textContent = count ? String(count) : '';
  if (s?.isImported) {
    showBanner('Viewing an imported trace. Data is read-only and timestamps come from the trace\'s own clock.', 'imported');
  } else if (!state.error) {
    hideBanner();
  }
}

function showBanner(text, kind) {
  const banner = $('banner');
  banner.hidden = false;
  banner.className = `banner ${kind || ''}`;
  banner.setAttribute('role', kind === 'error' ? 'alert' : 'status');
  clear(banner).appendChild(document.createTextNode(text));
}

function hideBanner() {
  $('banner').hidden = true;
}

function showRefreshHint() {
  if (state.pendingRefresh) {
    return;
  }

  state.pendingRefresh = true;
  const banner = $('banner');
  banner.hidden = false;
  banner.className = 'banner';
  clear(banner).append('New data is available. ', h('button', { type: 'button', class: 'btn small', onclick: () => render() }, 'Refresh view'));
}

function hideRefreshHint() {
  if (state.pendingRefresh) {
    state.pendingRefresh = false;
    hideBanner();
  }
}

// ----------------------------------------------------------------------------------------------- Drawer and details

function openDrawer(title, content) {
  lastFocus = document.activeElement;
  $('drawer-title').textContent = title;
  clear($('drawer-body')).appendChild(content);
  $('drawer').hidden = false;
  $('drawer-close').focus();
}

function closeDrawer() {
  $('drawer').hidden = true;
  if (lastFocus && lastFocus.focus) {
    lastFocus.focus();
  }
}

async function showEvent(e) {
  openDrawer('Event details', views.eventDetails(ctx, e, null));
  if (e.operationId && state.source) {
    try {
      const related = await state.source.operation(state.sessionId, e.operationId);
      clear($('drawer-body')).appendChild(views.eventDetails(ctx, e, related || []));
    } catch (error) {
      $('drawer-body').appendChild(h('p', { class: 'muted' }, `Related events unavailable: ${error.message}`));
    }
  }
}

// ----------------------------------------------------------------------------------------------- Actions

async function act(fn, message) {
  const source = state.source === state.fileSource ? state.fileSource : (state.liveSource || state.source);
  try {
    await fn(source);
    if (message) {
      showBanner(message, '');
    }

    // Invalidate in-flight polls too: their data was loaded before the action took effect.
    state.report = null;
    state.generation++;
    await poll();
    render();
  } catch (error) {
    showBanner(error.message, 'error');
  }
}

function importFile(localOnly) {
  const input = $('import-file');
  input.value = '';
  input.onchange = async () => {
    const file = input.files[0];
    if (!file) {
      return;
    }

    const live = state.liveSource;
    if (!localOnly && live && !live.readOnly && confirm('Import into the running profiler so it can be re-analyzed with current rules?\n\nChoose Cancel to open the file locally in this browser only (nothing is uploaded).')) {
      try {
        const session = await live.importTrace(file);
        showBanner(`Imported "${session.name}".`, '');
        state.source = live;
        await selectSession(session.id);
      } catch (error) {
        showBanner(`Import failed: ${error.message}`, 'error');
      }
    } else {
      await openLocalFile(file);
    }
  };
  input.click();
}

async function openLocalFile(file) {
  try {
    const id = await state.fileSource.add(file);
    state.source = state.fileSource;
    if (!state.liveSource) {
      const picker = clear($('source-picker'));
      picker.append(h('span', null, 'Offline viewer'), h('button', { type: 'button', class: 'btn small', onclick: () => importFile(true) }, 'Open trace…'));
    }

    await selectSession(id);
  } catch (error) {
    showBanner(`Could not open the trace: ${error.message}`, 'error');
  }
}

async function exportSession(id, format) {
  const source = id.startsWith('file-') ? state.fileSource : state.liveSource;
  try {
    showBanner('Preparing export…', '');
    const { blob, fileName } = await source.exportTrace(id, format);
    downloadBlob(blob, fileName);
    hideBanner();
  } catch (error) {
    showBanner(`Export failed: ${error.message}`, 'error');
  }
}

// ----------------------------------------------------------------------------------------------- Chrome and settings

function bindChrome() {
  $('session-select').addEventListener('change', e => selectSession(e.target.value));
  $('live-toggle').addEventListener('click', () => {
    state.live = !state.live;
    $('live-toggle').setAttribute('aria-pressed', String(state.live));
    $('live-toggle').textContent = state.live ? 'Live' : 'Paused';
    if (state.live) {
      schedulePoll(0);
    } else {
      clearTimeout(pollTimer);
    }
  });
  $('settings-toggle').addEventListener('click', () => {
    const panel = $('settings');
    panel.hidden = !panel.hidden;
    $('settings-toggle').setAttribute('aria-expanded', String(!panel.hidden));
  });
  $('drawer-close').addEventListener('click', closeDrawer);
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && !$('drawer').hidden) {
      closeDrawer();
    }
  });
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden && state.live) {
      schedulePoll(0);
    }
  });
}

function bindSettings() {
  const bind = (id, key, transform = v => v) => {
    const el = $(id);
    el.value = String(settings[key]);
    el.addEventListener('change', () => {
      settings[key] = transform(el.value);
      saveSettings();
      applyTheme();
      render();
    });
  };
  bind('setting-time-unit', 'timeUnit');
  bind('setting-byte-unit', 'byteUnit');
  bind('setting-range', 'rangeSeconds', Number);
  bind('setting-refresh', 'refreshMs', Number);
  bind('setting-theme', 'theme');
}

function applyTheme() {
  if (settings.theme === 'system') {
    document.documentElement.removeAttribute('data-theme');
  } else {
    document.documentElement.setAttribute('data-theme', settings.theme);
  }
}

init();
