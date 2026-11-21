// Blazor Profiler browser module.
// Observes browser performance entries and forwards them to .NET in small batches. In WebAssembly it also answers
// dashboard requests received over a same-origin BroadcastChannel. It never reads element content, input values,
// cookies, or storage.

const CHANNEL_NAME = 'blazor-profiler';
const MAX_BUFFER = 2000;
const MAX_ENTRIES_PER_BATCH = 1000;
const MAX_BATCHES_PER_FLUSH = 8;
// Blazor Server's default SignalR limit is 32 KB (UTF-8 bytes) per incoming message. Batches are measured in UTF-8 bytes
// of their escaped form (they travel as a string argument); the JSON hub protocol escapes them once more, which at most
// doubles the size, so the default budget leaves room for that and the invocation envelope.
const DEFAULT_MAX_BATCH_BYTES = 16000;
// How long after its input an interaction's record stays buffered, so a late interaction contentful paint can still be
// added to it. Content painted later than this is not attached.
const INTERACTION_SETTLE_MS = 5000;
// Scripts kept per long animation frame (BrowserLongTaskEvent.MaxScripts in .NET).
const MAX_FRAME_SCRIPTS = 5;
// Frustration signals: rage clicks are RAGE_CLICKS or more clicks within RAGE_WINDOW_MS and RAGE_RADIUS_PX of each other;
// a dead click is a click on an interactive element after which the DOM does not change and the URL stays the same for
// DEAD_CLICK_MS.
const RAGE_CLICKS = 3;
const RAGE_WINDOW_MS = 1000;
const RAGE_RADIUS_PX = 30;
const DEAD_CLICK_MS = 1000;
const INTERACTIVE_SELECTOR = 'button, a[href], input[type=button], input[type=submit], input[type=reset], input[type=image], [role=button], [role=link], [role=menuitem], [role=tab], summary';
// The interaction an entry was created for. A symbol key is not serialized by JSON.stringify.
const INTERACTION_ID = Symbol('interactionId');
// Blazor's own JS-to-.NET plumbing that goes through DotNetObjectReference instances: event dispatch (recorded as event
// handlers) and root component management. Calls made by framework components (Virtualize, InputFile) are kept.
const FRAMEWORK_DOTNET_METHODS = new Set(['DispatchEventAsync', 'AddRootComponent', 'RemoveRootComponent', 'SetRootComponentParameters']);
const WRAPPED = Symbol('blazorProfilerWrapped');
const MAX_PAYLOAD_ESTIMATE_BYTES = 16 * 1024 * 1024;
// The property holding the byte length of a JavaScript stream reference (DotNet.createJSStreamReference).
const JS_STREAM_LENGTH = '__jsStreamReferenceLength';

let state = null;
// performance.now() when the last collecting client stopped. The module lives as long as the document, so a client created
// later in it (for example after an enhanced navigation re-creates <BlazorProfilerClient>) observes with `buffered: true`
// and would otherwise report every entry since page load again.
let reportedUntil = -Infinity;
// Startup is reported once per document (enhanced navigations re-create the client without reloading the page).
let startupReported = false;
let firstInteractiveAt = null;

export function start(dotNetRef, optionsJson) {
  stop();
  const options = typeof optionsJson === 'string' ? JSON.parse(optionsJson) : (optionsJson || {});
  state = {
    ref: dotNetRef,
    owner: options.owner,
    options,
    buffer: [],
    observers: [],
    timer: 0,
    channel: null,
    clientId: (globalThis.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Math.random()).slice(2),
    keyHandlers: [],
    launcher: null,
    interactions: new Map(),
    // Interactions below the reporting threshold, kept briefly in case their content paints late.
    recent: new Map(),
    collect: options.collect !== false,
    toolbar: null,
    highlight: null,
    live: createLiveStats(),
    vitals: { lcp: null, cls: 0, clsSource: undefined, sent: {} },
  };

  // The toolbar shows live browser metrics, so it observes interactions and long frames even when collection is off.
  if (state.collect || options.toolbar) {
    observeInteractions(options.interactionThresholdMs || 40, !!options.toolbar);
    observeLongFrames();
  }

  if (state.collect) {
    if (options.dotNetCalls) {
      instrumentDotNetCalls(dotNetRef);
    }

    if (options.frustration !== false) {
      observeFrustration(options.frustrationIgnoreSelector);
    }

    observeResources();
    observeVitals();
    scheduleStartup();
    state.timer = setInterval(() => flush(false), options.flushIntervalMs || 2000);
  }

  if (options.bridge) {
    startBridge();
  }

  if (options.agentTools) {
    registerAgentTools();
  }

  if (options.shortcut) {
    installShortcut(options.shortcut, openDashboard);
  }

  if (options.highlightShortcut) {
    installShortcut(options.highlightShortcut, () => setHighlight(!state.highlight));
  }

  if (options.toolbar) {
    installToolbar();
  } else if (options.showLauncher) {
    installLauncher();
  }

  if (options.highlight || readPreference(HIGHLIGHT_KEY) === '1') {
    setHighlight(true);
  }

  return performance.timeOrigin + performance.now();
}

/**
 * Stops collection. `owner` is the value the client passed to start: a client disposed after another one started (the
 * module is shared by the whole document) must not stop its successor.
 */
export function stop(owner) {
  if (!state || (owner !== undefined && owner !== state.owner)) {
    return;
  }

  for (const { observer, handle } of state.observers) {
    // Entries already recorded but not yet delivered to the callback; reported now so the watermark below is accurate.
    try { handle(observer.takeRecords()); } catch { /* ignore */ }
    try { observer.disconnect(); } catch { /* ignore */ }
  }

  if (state.observers.length > 0) {
    reportedUntil = performance.now();
  }

  clearInterval(state.timer);
  clearTimeout(state.startupTimer);
  queueVitals();
  flush(true);
  if (state.channel) {
    state.channel.close();
  }

  if (state.agentTools) {
    // Unregisters the WebMCP tools.
    state.agentTools.abort();
  }

  for (const handler of state.keyHandlers) {
    window.removeEventListener('keydown', handler);
  }

  stopFrustration();

  if (state.launcher) {
    state.launcher.remove();
  }

  disableHighlight();
  removeToolbar();
  state = null;
}

export function openDashboard() {
  const url = state && state.options.dashboardUrl;
  if (!url) {
    return;
  }

  const target = new URL(url, document.baseURI);
  window.open(target.href, '_blank', 'noopener');
}

function supports(type) {
  return typeof PerformanceObserver !== 'undefined'
    && Array.isArray(PerformanceObserver.supportedEntryTypes)
    && PerformanceObserver.supportedEntryTypes.includes(type);
}

function observe(type, init, callback) {
  if (!supports(type)) {
    return;
  }

  // Entries that ended before an earlier client stopped were reported by it; skip them in the buffered replay.
  const handle = entries => {
    const fresh = entries.filter(e => e.startTime + e.duration > reportedUntil);
    if (fresh.length > 0) {
      callback(fresh);
    }
  };

  try {
    const observer = new PerformanceObserver(list => {
      try { handle(list.getEntries()); } catch { /* never break the page */ }
    });
    observer.observe({ type, buffered: true, ...init });
    state.observers.push({ observer, handle });
  } catch { /* unsupported options */ }
}

function push(entry) {
  if (!state || !state.collect) {
    return;
  }

  if (state.buffer.length >= MAX_BUFFER) {
    forget(state.buffer.shift());
  }

  state.buffer.push(entry);
}

// Stops tracking the interaction of an entry that left the buffer (sent or dropped), so later entries for the same
// interaction start a new record instead of updating one that will never be sent, and the map stays bounded by the buffer.
function forget(entry) {
  const id = entry && entry[INTERACTION_ID];
  if (id !== undefined && state.interactions.get(id) === entry) {
    state.interactions.delete(id);
  }
}

// The number of UTF-8 bytes of a string (what SignalR's message size limit measures), without allocating.
function utf8Length(text) {
  let bytes = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c < 0x80) {
      bytes += 1;
    } else if (c < 0x800) {
      bytes += 2;
    } else if (c >= 0xd800 && c <= 0xdbff && i + 1 < text.length && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00) {
      bytes += 4;
      i++;
    } else {
      // Other BMP characters, and lone surrogates (encoded as U+FFFD).
      bytes += 3;
    }
  }

  return bytes;
}

function epoch(startTime) {
  return performance.timeOrigin + startTime;
}

// Long strings are cut here as well as in .NET, so a single entry can never exceed the batch budget.
function cut(value, max) {
  return typeof value === 'string' && value.length > max ? value.slice(0, max) : value;
}

/**
 * Records JavaScript-to-.NET calls: DotNetObjectReference.invokeMethod(Async) (instance methods, found through the
 * prototype of the profiler's own reference, which every reference shares) and DotNet.invokeMethod(Async) (static methods).
 * The wrappers are installed once per document and record only while a client collects; they never change arguments,
 * results, or errors. The duration runs from the call until .NET returns (asynchronous calls: until the returned promise
 * settles), which on Blazor Server includes the round trip over the circuit.
 */
function instrumentDotNetCalls(ref) {
  try {
    const proto = ref && Object.getPrototypeOf(ref);
    if (proto && proto !== Object.prototype) {
      wrapDotNetMethod(proto, 'invokeMethodAsync', true, true);
      wrapDotNetMethod(proto, 'invokeMethod', true, false);
    }

    const dotNet = globalThis.DotNet;
    if (dotNet && typeof dotNet === 'object') {
      wrapDotNetMethod(dotNet, 'invokeMethodAsync', false, true);
      wrapDotNetMethod(dotNet, 'invokeMethod', false, false);
    }
  } catch { /* never break the page */ }
}

function wrapDotNetMethod(target, name, instance, async) {
  const original = target[name];
  if (typeof original !== 'function' || original[WRAPPED]) {
    return;
  }

  const wrapper = function (...args) {
    const current = state;
    const method = instance ? args[0] : args[1];
    if (!current || !current.collect || !current.options.dotNetCalls || (instance && this === current.ref)
      || typeof method !== 'string' || method.startsWith('BlazorProfiler.') || (instance && FRAMEWORK_DOTNET_METHODS.has(method))) {
      return original.apply(this, args);
    }

    const identifier = instance ? method : `${method} (${args[0]})`;
    const argumentCount = Math.max(0, args.length - (instance ? 1 : 2));
    const payload = current.options.estimatePayload ? estimateArguments(args, instance ? 1 : 2) : undefined;
    const streamed = streamBytes(args, instance ? 1 : 2);
    const started = performance.now();
    const record = error => {
      try {
        push({
          k: 'x',
          n: cut(identifier, 256),
          t: epoch(started),
          d: Math.max(0, performance.now() - started),
          c: argumentCount,
          sy: async ? 0 : 1,
          or: instance ? 1 : 0,
          er: error === undefined ? undefined : cut((error && error.name) || 'Error', 64),
          pb: payload,
          sb: streamed,
        });
      } catch { /* never break the page */ }
    };

    let result;
    try {
      result = original.apply(this, args);
    } catch (error) {
      record(error || null);
      throw error;
    }

    if (async && result && typeof result.then === 'function') {
      // A separate chain: the caller still receives the original promise and its rejection.
      result.then(() => record(undefined), error => record(error || null));
    } else {
      record(undefined);
    }

    return result;
  };

  wrapper[WRAPPED] = true;
  try {
    target[name] = wrapper;
  } catch { /* read-only: not recorded */ }
}

// The bytes of JavaScript stream references (DotNet.createJSStreamReference) among the arguments, which .NET reads
// separately; undefined when there are none. Arrays are searched one level deep.
function streamBytes(args, skip) {
  let total;
  const add = value => {
    const length = value && typeof value === 'object' ? value[JS_STREAM_LENGTH] : undefined;
    if (typeof length === 'number' && length >= 0) {
      total = (total || 0) + length;
    }
  };

  for (let i = skip; i < args.length; i++) {
    add(args[i]);
    if (Array.isArray(args[i])) {
      for (let j = 0; j < Math.min(args[i].length, 100); j++) {
        add(args[i][j]);
      }
    }
  }

  return total;
}

// The JSON size of the arguments in UTF-8 bytes; undefined when they cannot be serialized here (cycles) or are huge.
function estimateArguments(args, skip) {
  try {
    let bytes = 0;
    for (let i = skip; i < args.length; i++) {
      const json = JSON.stringify(args[i]);
      bytes += json === undefined ? 4 : utf8Length(json) + 1;
      if (bytes > MAX_PAYLOAD_ESTIMATE_BYTES) {
        return MAX_PAYLOAD_ESTIMATE_BYTES;
      }
    }

    return bytes;
  } catch {
    return undefined;
  }
}

function observeInteractions(threshold, live) {
  // Interaction contentful paint (Chromium): when the content an interaction changed was painted, including content
  // rendered after asynchronous work (an awaited WebAssembly handler). Interactions faster than the threshold are kept
  // aside (state.recent) so a late content paint can still report them.
  const contentPaint = supports('interaction-contentful-paint');

  // The toolbar estimates INP from every interaction, so it lowers the observation threshold to the minimum (16 ms).
  observe('event', { durationThreshold: live || contentPaint ? 16 : Math.max(16, threshold) }, entries => {
    for (const e of entries) {
      if (e.interactionId) {
        recordLiveInteraction(e);
      }

      // Only discrete interactions carry an interactionId; keep the longest entry per interaction.
      if (!e.interactionId || !state.collect) {
        continue;
      }

      const existing = state.interactions.get(e.interactionId) || state.recent.get(e.interactionId);
      if (existing && existing.d >= e.duration) {
        continue;
      }

      const entry = {
        k: 'i',
        n: cut(e.name, 64),
        t: epoch(e.startTime),
        d: e.duration,
        id: Math.max(0, e.processingStart - e.startTime),
        pr: Math.max(0, e.processingEnd - e.processingStart),
        pd: Math.max(0, e.startTime + e.duration - e.processingEnd),
        tag: e.target && e.target.tagName ? cut(e.target.tagName.toLowerCase(), 32) : undefined,
      };

      if (existing) {
        // Still buffered or set aside: update it in place (keeping a content paint already recorded).
        Object.assign(existing, entry);
      } else {
        entry[INTERACTION_ID] = e.interactionId;
      }

      const record = existing || entry;
      if (state.interactions.get(e.interactionId) !== record) {
        if (record.d >= threshold || (record.cp || 0) >= threshold) {
          report(record, e.interactionId);
        } else {
          rememberRecent(e.interactionId, record);
        }
      }
    }
  });

  if (contentPaint) {
    observe('interaction-contentful-paint', {}, entries => {
      for (const e of entries) {
        if (!e.interactionId || !state.collect) {
          continue;
        }

        // Several entries can arrive per interaction as more content paints; the latest (largest) one counts.
        const record = state.interactions.get(e.interactionId) || state.recent.get(e.interactionId);
        if (!record) {
          continue;
        }

        record.cp = Math.max(record.cp || 0, e.duration);
        if (state.interactions.get(e.interactionId) !== record && record.cp >= threshold) {
          // Reported only because its content painted late: kept out of the interaction latency statistics, so they do
          // not depend on whether the browser supports content paint.
          record.co = record.d < threshold ? 1 : undefined;
          report(record, e.interactionId);
        }
      }
    });
  }
}

function report(record, interactionId) {
  state.recent.delete(interactionId);
  state.interactions.set(interactionId, record);
  push(record);
}

function rememberRecent(interactionId, record) {
  state.recent.set(interactionId, record);
  if (state.recent.size > 100) {
    state.recent.delete(state.recent.keys().next().value);
  }
}

function observeLongFrames() {
  if (supports('long-animation-frame')) {
    observe('long-animation-frame', {}, entries => {
      for (const e of entries) {
        recordLiveLongFrame(e);
        let script;
        let scripts;
        let forced = 0;
        if (Array.isArray(e.scripts) && e.scripts.length > 0) {
          const longest = e.scripts.reduce((a, b) => (b.duration > a.duration ? b : a));
          script = longest.sourceURL || longest.invoker || longest.name || undefined;
          for (const s of e.scripts) {
            forced += s.forcedStyleAndLayoutDuration || 0;
          }

          // Long Animation Frame script attribution: the longest scripts with invoker, source location, and forced layout.
          // Origin-trial builds named the invoker `name`. Script contents are never read.
          scripts = [...e.scripts]
            .sort((a, b) => b.duration - a.duration)
            .slice(0, MAX_FRAME_SCRIPTS)
            .map(s => ({
              inv: cut(s.invoker || s.name || undefined, 256),
              it: cut(s.invokerType || s.type || undefined, 32),
              src: cut(s.sourceURL || undefined, 512),
              fn: cut(s.sourceFunctionName || undefined, 128),
              cp: typeof s.sourceCharPosition === 'number' ? s.sourceCharPosition : -1,
              d: s.duration || 0,
              fl: s.forcedStyleAndLayoutDuration || 0,
              p: s.pauseDuration || 0,
            }));
        }

        const end = e.startTime + e.duration;
        push({
          k: 'l',
          n: 'long-animation-frame',
          t: epoch(e.startTime),
          d: e.duration,
          b: e.blockingDuration || 0,
          s: cut(script, 512),
          fl: forced,
          rd: e.renderStart > 0 ? Math.max(0, end - e.renderStart) : 0,
          sl: e.styleAndLayoutStart > 0 ? Math.max(0, end - e.styleAndLayoutStart) : 0,
          sc: scripts,
        });
      }
    });
  } else {
    observe('longtask', {}, entries => {
      for (const e of entries) {
        recordLiveLongFrame(e);
        push({ k: 'l', n: 'longtask', t: epoch(e.startTime), d: e.duration, b: Math.max(0, e.duration - 50) });
      }
    });
  }
}

// Describes an element structurally (tag, id, up to two classes), never by its text or other attributes. Blazor's CSS
// isolation scope attributes are not classes, so they never appear.
function describeElement(el) {
  if (!el || el.nodeType !== 1) {
    return undefined;
  }

  let text = el.tagName.toLowerCase();
  if (el.id) {
    text += '#' + cut(el.id, 48);
  }

  const classes = typeof el.className === 'string' ? el.className.split(/\s+/).filter(c => c).sort().slice(0, 2) : [];
  for (const c of classes) {
    text += '.' + cut(c, 32);
  }

  return cut(text, 160);
}

// Rage and dead clicks (frustration signals). A click listener in the capture phase records positions; a mutation
// observer records when the DOM last changed. Nothing about the element except its structure (describeElement) is kept.
function observeFrustration(ignoreSelector) {
  if (typeof document === 'undefined' || typeof MutationObserver === 'undefined') {
    return;
  }

  // mutations: times (performance.now) at which the DOM changed, at most one per 16 ms and only while clicks are pending.
  const f = { clicks: [], pending: [], observer: null, mutations: [], ignore: ignoreSelector || null, handler: null, timer: 0 };
  f.observer = new MutationObserver(records => {
    // Mutations only matter while a click waits for its verdict; otherwise the callback returns at once.
    if (f.pending.length === 0) {
      return;
    }

    for (const r of records) {
      if (isOwnNode(r.target)) {
        continue;
      }

      if (r.type === 'childList' && [...r.addedNodes, ...r.removedNodes].every(isOwnNode)) {
        continue;
      }

      // At most one entry per 16 ms, except that the first change after the newest click is always recorded (it may be
      // that click's only effect).
      const now = performance.now();
      const last = f.mutations[f.mutations.length - 1];
      const newestClick = f.pending[f.pending.length - 1].t;
      if (last === undefined || now - last >= 16 || last < newestClick) {
        f.mutations.push(now);
      }

      return;
    }
  });

  f.handler = event => {
    try {
      if (!state || !event.isTrusted || event.button !== 0) {
        return;
      }

      const raw = event.target && event.target.nodeType === 1 ? event.target : event.target && event.target.parentElement;
      if (!raw || raw.closest('[data-blazor-profiler]')) {
        return;
      }

      if (f.ignore) {
        try {
          if (raw.closest(f.ignore)) {
            return;
          }
        } catch {
          f.ignore = null;
        }
      }

      const now = performance.now();
      const interactive = raw.closest(INTERACTIVE_SELECTOR);
      const el = interactive || raw;
      const target = describeElement(el);

      // Rage clicks: clicks on one spot, each within a window of the previous one, form a burst; the burst is a rage
      // click when RAGE_CLICKS of them fall within one window (so steady clicking every 900 ms is not rage). Clicks
      // activated from the keyboard (detail 0) have no position and are left out.
      let burst = null;
      if (event.detail !== 0) {
        f.clicks = f.clicks.filter(c => now - c.t <= RAGE_WINDOW_MS);
        for (let i = f.clicks.length - 1; i >= 0; i--) {
          const c = f.clicks[i];
          if (Math.abs(c.x - event.clientX) <= RAGE_RADIUS_PX && Math.abs(c.y - event.clientY) <= RAGE_RADIUS_PX) {
            burst = c.burst;
            break;
          }
        }

        if (!burst) {
          burst = { start: now, last: now, times: [], peak: 0, target, tag: el.tagName.toLowerCase(), scheduled: false, reported: false };
        }

        if (burst.times.length < 1000) {
          burst.times.push(now);
        }

        burst.last = now;
        burst.peak = Math.max(burst.peak, burst.times.filter(t => now - t <= RAGE_WINDOW_MS).length);
        f.clicks.push({ t: now, x: event.clientX, y: event.clientY, burst });
        if (burst.peak >= RAGE_CLICKS && !burst.scheduled) {
          burst.scheduled = true;
          scheduleBurstReport(f, burst);
        }
      }

      // Dead clicks: interactive elements only, and not disabled ones (a disabled button is expected to do nothing).
      if (interactive && !interactive.disabled && interactive.getAttribute('aria-disabled') !== 'true') {
        f.pending.push({ t: now, target, tag: interactive.tagName.toLowerCase(), url: location.href, burst });
        if (f.pending.length > 100) {
          f.pending.shift();
        }

        if (!f.timer) {
          f.timer = setTimeout(() => checkDeadClicks(f), DEAD_CLICK_MS + 50);
        }
      }
    } catch {
      // Never break the page.
    }
  };

  f.observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
  document.addEventListener('click', f.handler, { capture: true, passive: true });
  state.frustration = f;
}

function scheduleBurstReport(f, burst) {
  const check = () => {
    if (!state || state.frustration !== f || burst.reported) {
      return;
    }

    // Wait until the burst has been quiet for one window, so its full click count is reported.
    const quiet = performance.now() - burst.last;
    if (quiet < RAGE_WINDOW_MS) {
      setTimeout(check, RAGE_WINDOW_MS - quiet + 10);
      return;
    }

    burst.reported = true;
    push({ k: 'f', n: 'rage', t: epoch(burst.start), d: Math.max(0, burst.last - burst.start), c: burst.times.length, tag: cut(burst.tag, 32), tg: burst.target });
  };

  setTimeout(check, RAGE_WINDOW_MS + 10);
}

function checkDeadClicks(f) {
  f.timer = 0;
  if (!state || state.frustration !== f) {
    return;
  }

  const now = performance.now();
  const remaining = [];
  for (const click of f.pending) {
    // Wait for the click's window, and for its burst to end: a burst can still become a rage click.
    if (now - click.t < DEAD_CLICK_MS || (click.burst && now - click.burst.last < RAGE_WINDOW_MS)) {
      remaining.push(click);
      continue;
    }

    // Only changes within the click's window count: a later, unrelated update must not hide a dead click.
    const changed = f.mutations.some(m => m >= click.t && m <= click.t + DEAD_CLICK_MS) || location.href !== click.url;
    // Clicks of a rage burst are reported as the burst, not again one by one.
    if (!changed && !(click.burst && click.burst.peak >= RAGE_CLICKS)) {
      push({ k: 'f', n: 'dead', t: epoch(click.t), d: 0, c: 1, tag: cut(click.tag, 32), tg: click.target });
    }
  }

  f.pending = remaining;
  const oldest = remaining.length > 0 ? remaining[0].t : Infinity;
  f.mutations = f.mutations.filter(m => m >= oldest);
  if (remaining.length > 0) {
    f.timer = setTimeout(() => checkDeadClicks(f), DEAD_CLICK_MS + 50);
  }
}

function stopFrustration() {
  const f = state && state.frustration;
  if (!f) {
    return;
  }

  f.observer.disconnect();
  document.removeEventListener('click', f.handler, { capture: true });
  clearTimeout(f.timer);
  state.frustration = null;
}

function observeResources() {
  observe('resource', {}, entries => {
    for (const e of entries) {
      push({
        k: 'r',
        n: cut(e.name, 2048),
        t: epoch(e.startTime),
        d: e.duration,
        it: cut(e.initiatorType, 32),
        ts: e.transferSize || 0,
        ds: e.decodedBodySize || 0,
        st: e.responseStatus || 0,
      });
    }
  });
}

// Sends buffered entries in batches whose serialized size stays within the transport budget (on Blazor Server an
// oversized message would close the circuit). A periodic flush sends at most MAX_BATCHES_PER_FLUSH batches and leaves the
// rest for the next tick; the final flush on stop sends everything.
function flush(all) {
  queueVitals();
  if (!state || state.buffer.length === 0) {
    return;
  }

  // Interactions younger than INTERACTION_SETTLE_MS stay for the next flush, so a content paint that arrives later can
  // still be added to them (the final flush sends everything).
  let held = [];
  if (!all) {
    const now = performance.timeOrigin + performance.now();
    held = state.buffer.filter(e => e.k === 'i' && now - e.t < INTERACTION_SETTLE_MS);
    if (held.length > 0) {
      state.buffer = state.buffer.filter(e => !held.includes(e));
    }
  }

  const budget = state.options.maxBatchBytes || DEFAULT_MAX_BATCH_BYTES;
  for (let batches = 0; state.buffer.length > 0 && (all || batches < MAX_BATCHES_PER_FLUSH); batches++) {
    const parts = [];
    let size = 2;
    let taken = 0;
    while (taken < state.buffer.length && parts.length < MAX_ENTRIES_PER_BATCH) {
      const entry = state.buffer[taken];
      const text = JSON.stringify(entry);
      // The batch is passed as a string argument, so measure the UTF-8 length of the escaped entry (plus a separator).
      const cost = utf8Length(JSON.stringify(text)) - 1;
      if (cost > budget) {
        // Cannot be sent without exceeding the budget; drop it (entries are truncated, so this is not expected).
        taken++;
        continue;
      }

      if (parts.length > 0 && size + cost > budget) {
        break;
      }

      parts.push(text);
      size += cost;
      taken++;
    }

    // Interactions already sent (or dropped) can no longer be updated in place; later entries for them start a new record.
    for (const entry of state.buffer.splice(0, taken)) {
      forget(entry);
    }

    if (parts.length > 0) {
      state.ref.invokeMethodAsync('BlazorProfiler.Ingest', `[${parts.join(',')}]`).catch(() => { /* circuit gone */ });
    }
  }

  state.buffer.push(...held);
}

function startBridge() {
  if (typeof BroadcastChannel === 'undefined') {
    return;
  }

  const channel = new BroadcastChannel(CHANNEL_NAME);
  state.channel = channel;
  const announce = () => channel.postMessage({
    type: 'announce',
    clientId: state.clientId,
    title: document.title,
    path: location.pathname,
    source: state.options.source || 'webassembly',
  });

  channel.onmessage = async event => {
    const message = event.data || {};
    if (message.type === 'discover') {
      announce();
      return;
    }

    if (message.type !== 'request' || message.clientId !== state.clientId) {
      return;
    }

    const { ref, clientId } = state;
    let payload;
    try {
      payload = await invokeApi(ref, JSON.stringify(message.payload || {}));
    } catch (error) {
      payload = JSON.stringify({ ok: false, error: String(error && error.message || error) });
    }

    channel.postMessage({ type: 'response', clientId, requestId: message.requestId, payload });
  };

  announce();
  window.addEventListener('pagehide', () => {
    try { channel.postMessage({ type: 'goodbye', clientId: state && state.clientId }); } catch { /* ignore */ }
  });
}

// In-page agent tools (WebMCP): the profiler's read-only MCP tools, offered to AI agents in the browser through
// document.modelContext where the browser supports it. Requests reach the profiler through the dashboard bridge (in-process
// in WebAssembly, the circuit on Blazor Server), or on Blazor Server through the MCP endpoint, with the same access rules.
const AGENT_TOOL_PREFIX = 'blazor_profiler_';
let mcpRequestId = 0;

async function callMcp(method, params) {
  const message = { jsonrpc: '2.0', id: ++mcpRequestId, method, params };
  let reply;
  if (state.options.bridge) {
    const response = JSON.parse(await invokeApi(state.ref, JSON.stringify({ op: 'mcp', mcpMessage: JSON.stringify(message) })));
    if (!response.ok) {
      throw new Error(response.error || 'The profiler refused the request.');
    }

    reply = JSON.parse(response.mcp || 'null');
  } else if (state.options.mcpUrl) {
    const response = await fetch(new URL(state.options.mcpUrl, document.baseURI), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(message),
      credentials: 'same-origin',
    });
    if (!response.ok) {
      throw new Error(`The profiler's MCP endpoint answered ${response.status}.`);
    }

    reply = await response.json();
  } else {
    throw new Error('The profiler is not reachable from this page (enable the dashboard bridge or the MCP endpoint).');
  }

  if (!reply || reply.error) {
    throw new Error((reply && reply.error && reply.error.message) || 'The profiler returned no answer.');
  }

  return reply.result;
}

async function registerAgentTools() {
  const context = document.modelContext;
  if (!context || typeof context.registerTool !== 'function' || typeof AbortController === 'undefined') {
    return;
  }

  const owner = state;
  let tools;
  try {
    tools = (await callMcp('tools/list', {})).tools || [];
  } catch {
    // No access from this page (for example a remote origin on Blazor Server): no tools.
    return;
  }

  if (state !== owner) {
    return;
  }

  const controller = new AbortController();
  state.agentTools = controller;
  for (const tool of tools) {
    // Only tools that read: the page's agent must not change what is being profiled.
    if (!tool.annotations || tool.annotations.readOnlyHint !== true) {
      continue;
    }

    try {
      await context.registerTool({
        name: AGENT_TOOL_PREFIX + tool.name,
        title: tool.title ? `Blazor Profiler: ${tool.title}` : undefined,
        description: `${tool.description} (Blazor Profiler: performance data recorded from this app.)`,
        inputSchema: tool.inputSchema || { type: 'object', properties: {} },
        // Results contain names and URLs recorded from the app: data, not instructions.
        annotations: { readOnlyHint: true, untrustedContentHint: true, debugging: true },
        async execute(input) {
          if (!state) {
            throw new Error('The profiler was stopped.');
          }

          const result = await callMcp('tools/call', { name: tool.name, arguments: input || {} });
          const text = (result.content || []).filter(c => c.type === 'text').map(c => c.text).join('\n\n');
          if (result.isError) {
            throw new Error(text || 'The tool failed.');
          }

          return text;
        },
      }, { signal: controller.signal });
    } catch {
      // A tool the browser rejects (or one registered twice) is skipped; the others stay available.
    }
  }
}

// Sends a dashboard request to .NET. A request above the transport budget (on Blazor Server, SignalR's message size limit,
// reached for example by a trace import) is streamed in chunks instead: a single oversized message would close the circuit.
function invokeApi(ref, json) {
  const limit = state && state.options.maxRequestBytes;
  if (!limit || utf8Length(JSON.stringify(json)) <= limit) {
    return ref.invokeMethodAsync('BlazorProfiler.Api', json);
  }

  const dotNet = globalThis.DotNet;
  if (!dotNet || typeof dotNet.createJSStreamReference !== 'function') {
    return Promise.resolve(JSON.stringify({ ok: false, error: 'The request is too large to send to the app.' }));
  }

  return ref.invokeMethodAsync('BlazorProfiler.ApiStream', dotNet.createJSStreamReference(new TextEncoder().encode(json)));
}

function installShortcut(shortcut, action) {
  const parts = shortcut.toLowerCase().split('+').map(p => p.trim());
  const key = parts[parts.length - 1];
  const handler = e => {
    if (!state) {
      return;
    }

    const match = (e.key || '').toLowerCase() === key
      && e.altKey === parts.includes('alt')
      && e.shiftKey === parts.includes('shift')
      && e.ctrlKey === parts.includes('ctrl')
      && e.metaKey === parts.includes('meta');
    if (match) {
      e.preventDefault();
      action();
    }
  };

  window.addEventListener('keydown', handler);
  state.keyHandlers.push(handler);
}

function installLauncher() {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = 'Profiler';
  button.setAttribute('aria-label', 'Open Blazor Profiler dashboard');
  button.style.cssText = 'position:fixed;right:12px;bottom:12px;z-index:2147483647;padding:6px 10px;border-radius:6px;'
    + 'border:1px solid #888;background:#1f2937;color:#fff;font:12px system-ui,sans-serif;cursor:pointer;opacity:.8';
  button.addEventListener('click', openDashboard);
  document.body.appendChild(button);
  state.launcher = button;
}

// ------------------------------------------------------------------------------------------------ Live metrics, toolbar
// Everything below runs only in the browser: it adds no .NET work, no interop calls, and (on Blazor Server) no circuit
// traffic, so it can stay on while measuring.

const HIGHLIGHT_KEY = 'blazor-profiler:highlight';
const MAX_LIVE_INTERACTIONS = 500;
const LIVE_WINDOW_MS = 10000;
// Web Vitals thresholds for Interaction to Next Paint.
const INP_GOOD_MS = 200;
const INP_POOR_MS = 500;

function readPreference(key) {
  try { return sessionStorage.getItem(key); } catch { return null; }
}

function writePreference(key, value) {
  try { sessionStorage.setItem(key, value); } catch { /* storage unavailable */ }
}

function createLiveStats() {
  return { interactions: new Map(), count: 0, last: null, longFrames: [], mutations: [] };
}

function recordLiveInteraction(e) {
  const live = state && state.live;
  if (!live) {
    return;
  }

  const previous = live.interactions.get(e.interactionId);
  if (previous === undefined) {
    live.count++;
    if (live.interactions.size >= MAX_LIVE_INTERACTIONS) {
      live.interactions.delete(live.interactions.keys().next().value);
    }
  }

  if (previous === undefined || e.duration > previous) {
    live.interactions.set(e.interactionId, e.duration);
  }

  live.last = { duration: Math.max(previous || 0, e.duration), name: e.name };
}

function recordLiveLongFrame(e) {
  if (state && state.live) {
    state.live.longFrames.push({ t: e.startTime + e.duration, d: e.duration });
  }
}

/**
 * Estimates Interaction to Next Paint the way the web-vitals library does: the worst interaction, ignoring one of every
 * 50 interactions (a high percentile on long sessions). Interactions under 16 ms are not reported by the browser.
 */
export function estimateInp(durations, totalCount) {
  if (!durations.length) {
    return null;
  }

  const sorted = [...durations].sort((a, b) => b - a);
  return sorted[Math.min(sorted.length - 1, Math.floor(totalCount / 50))];
}

function rating(ms) {
  return ms == null ? 'none' : ms <= INP_GOOD_MS ? 'good' : ms <= INP_POOR_MS ? 'warn' : 'poor';
}

function fmt(ms) {
  return ms == null ? '—' : ms >= 1000 ? `${(ms / 1000).toFixed(2)} s` : `${Math.round(ms)} ms`;
}

const TOOLBAR_CSS = `
:host { all: initial; }
.bar { position: fixed; right: 12px; bottom: 12px; z-index: 2147483647; display: flex; align-items: center; gap: 2px;
  font: 12px/1.2 system-ui, -apple-system, "Segoe UI", sans-serif; color: #e5e7eb; background: #111827ee;
  border: 1px solid #374151; border-radius: 8px; padding: 3px; box-shadow: 0 2px 10px #0006; max-width: calc(100vw - 24px);
  flex-wrap: wrap; }
.m { padding: 3px 7px; border-radius: 5px; white-space: nowrap; font-variant-numeric: tabular-nums; }
.m b { font-weight: 600; margin-left: 3px; }
.good b { color: #4ade80; } .warn b { color: #fbbf24; } .poor b { color: #f87171; } .none b { color: #9ca3af; }
button { all: unset; cursor: pointer; padding: 3px 8px; border-radius: 5px; color: #e5e7eb; white-space: nowrap; }
button:hover { background: #374151; }
button:focus-visible { outline: 2px solid #60a5fa; }
button[aria-pressed="true"] { background: #5b3fd1; color: #fff; }
.sep { width: 1px; align-self: stretch; background: #374151; margin: 2px; }
`;

function installToolbar() {
  const host = document.createElement('div');
  host.setAttribute('data-blazor-profiler', 'toolbar');
  const root = host.attachShadow({ mode: 'open' });
  const style = document.createElement('style');
  style.textContent = TOOLBAR_CSS;
  const bar = document.createElement('div');
  bar.className = 'bar';
  bar.setAttribute('role', 'toolbar');
  bar.setAttribute('aria-label', 'Blazor Profiler');

  const metric = title => {
    const el = document.createElement('span');
    el.className = 'm none';
    el.title = title;
    bar.appendChild(el);
    return el;
  };

  const inp = metric('Estimated Interaction to Next Paint for this page: the worst input-to-paint latency, ignoring one of every 50 interactions. Good: 200 ms or less; poor: above 500 ms.');
  const last = metric('Input-to-paint latency of the most recent interaction (Event Timing API).');
  const frames = metric('Long animation frames (main thread blocked for 50 ms or more) in the last 10 seconds.');
  const dom = metric('DOM mutations per second (Blazor render batches patch the DOM). Counted while update highlighting is on.');
  const sep = document.createElement('span');
  sep.className = 'sep';
  bar.appendChild(sep);

  const highlight = document.createElement('button');
  highlight.type = 'button';
  highlight.textContent = 'Highlight updates';
  highlight.title = 'Outline DOM elements as Blazor updates them. Colors go from blue to red as the same element updates repeatedly within a second.';
  highlight.addEventListener('click', () => setHighlight(!state.highlight));
  bar.appendChild(highlight);

  if (state.options.dashboardUrl) {
    const open = document.createElement('button');
    open.type = 'button';
    open.textContent = 'Dashboard';
    open.title = 'Open the Blazor Profiler dashboard in a new tab.';
    open.addEventListener('click', openDashboard);
    bar.appendChild(open);
  }

  installCaptureButtons(bar);
  root.append(style, bar);
  document.body.appendChild(host);

  const set = (el, label, value, kind) => {
    el.className = `m ${kind}`;
    el.textContent = label;
    const strong = document.createElement('b');
    strong.textContent = value;
    el.appendChild(strong);
  };

  const update = () => {
    if (!state) {
      return;
    }

    const live = state.live;
    const now = performance.now();
    live.longFrames = live.longFrames.filter(f => now - f.t <= LIVE_WINDOW_MS);
    live.mutations = live.mutations.filter(m => now - m.t <= 1000);
    const estimate = estimateInp([...live.interactions.values()], live.count);
    set(inp, 'INP', fmt(estimate), rating(estimate));
    set(last, 'Last', live.last ? fmt(live.last.duration) : '—', rating(live.last && live.last.duration));
    set(frames, 'Long frames', String(live.longFrames.length), live.longFrames.length === 0 ? 'good' : live.longFrames.length < 5 ? 'warn' : 'poor');
    const perSecond = live.mutations.reduce((n, m) => n + m.n, 0);
    set(dom, 'DOM', state.highlight ? `${perSecond}/s` : 'off', state.highlight ? (perSecond > 500 ? 'warn' : 'good') : 'none');
    highlight.setAttribute('aria-pressed', String(!!state.highlight));
    if (!host.isConnected) {
      // Enhanced navigation in a Blazor Web App can replace the body content; put the toolbar back.
      document.body.appendChild(host);
    }
  };

  update();
  state.toolbar = { host, timer: setInterval(update, 1000), update };
}

// ------------------------------------------------------------------------------------------- Deep capture (WebAssembly)

// The .NET WebAssembly runtime's diagnostics API (CPU samples, GC dump, runtime counters as .nettrace files). The functions
// are null unless the app is built with <EnableDiagnostics>true</EnableDiagnostics> (CPU samples also need
// <WasmPerformanceInstrumentation>), so the capture buttons only appear in such builds.
const CAPTURES = {
  cpu: { method: 'collectCpuSamples', label: 'CPU', title: 'Record .NET CPU samples for 10 seconds and download a .nettrace file (open it in PerfView or Visual Studio). Requires EnableDiagnostics and WasmPerformanceInstrumentation.', timed: true },
  gc: { method: 'collectGcDump', label: 'GC dump', title: 'Download a .NET heap snapshot (.nettrace; convert with dotnet-gcdump or open in PerfView). Requires EnableDiagnostics.', timed: false },
  counters: { method: 'collectMetrics', label: 'Counters', title: 'Record .NET runtime counters for 10 seconds and download a .nettrace file. Requires EnableDiagnostics.', timed: true },
};

function dotnetRuntime() {
  try {
    const get = globalThis.getDotnetRuntime;
    return typeof get === 'function' ? get(0) : null;
  } catch {
    return null;
  }
}

/** The deep captures available in this page: the keys of CAPTURES whose runtime function exists. */
export function availableCaptures() {
  const runtime = dotnetRuntime();
  return runtime ? Object.keys(CAPTURES).filter(k => typeof runtime[CAPTURES[k].method] === 'function') : [];
}

/**
 * Runs a deep capture with the .NET runtime's diagnostics API. The runtime downloads the .nettrace file itself; the capture
 * is also recorded as a marker on the profiler timeline. Resolves to a short description of the outcome.
 */
export async function captureDiagnostics(kind, seconds = 10) {
  const capture = CAPTURES[kind];
  const runtime = dotnetRuntime();
  if (!capture || !runtime || typeof runtime[capture.method] !== 'function') {
    throw new Error('Not available: build the app with <EnableDiagnostics>true</EnableDiagnostics>.');
  }

  const started = performance.now();
  try {
    const result = await runtime[capture.method](capture.timed ? { durationSeconds: seconds } : {});
    const bytes = Array.isArray(result) ? result.reduce((n, part) => n + ((part && part.length) || 0), 0) : 0;
    const text = `${capture.label} capture${capture.timed ? ` (${seconds} s)` : ''}: .nettrace downloaded${bytes ? ` (${Math.round(bytes / 1024)} KB)` : ''}`;
    push({ k: 'm', n: cut(text, 128), t: epoch(started), d: performance.now() - started });
    return text;
  } catch (error) {
    const text = `${capture.label} capture failed: ${error && error.message ? error.message : error}`;
    push({ k: 'm', n: cut(text, 128), t: epoch(started), d: performance.now() - started });
    throw new Error(text);
  }
}

function installCaptureButtons(bar) {
  const kinds = availableCaptures();
  if (kinds.length === 0) {
    return;
  }

  const sep = document.createElement('span');
  sep.className = 'sep';
  bar.appendChild(sep);
  const buttons = [];
  for (const kind of kinds) {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = CAPTURES[kind].label;
    button.title = CAPTURES[kind].title;
    button.addEventListener('click', async () => {
      for (const b of buttons) {
        b.disabled = true;
      }

      const label = button.textContent;
      button.textContent = CAPTURES[kind].timed ? `${label}…` : 'Dumping…';
      try {
        button.title = await captureDiagnostics(kind);
      } catch (error) {
        button.title = error.message;
      } finally {
        button.textContent = label;
        for (const b of buttons) {
          b.disabled = false;
        }
      }
    });
    buttons.push(button);
    bar.appendChild(button);
  }
}

function removeToolbar() {
  if (state && state.toolbar) {
    clearInterval(state.toolbar.timer);
    state.toolbar.host.remove();
    state.toolbar = null;
  }
}

// ------------------------------------------------------------------------------------------------ Update highlighting
// Outlines DOM elements as they change. Blazor components render into logical regions without a DOM element of their own,
// so this shows what each render batch actually patched in the DOM: a component that re-renders without changing its
// output patches nothing and does not flash (the dashboard reports such renders as "identical output").

const FLASH_MS = 800;
const MAX_BOXES_PER_FRAME = 300;
const REPEAT_WINDOW_MS = 1000;
const HEAT = ['#38bdf8', '#22d3ee', '#a3e635', '#facc15', '#fb923c', '#f87171'];

/** Turns update highlighting on or off; returns whether it is on. The choice is remembered for the browser tab. */
export function setHighlight(on) {
  if (!state) {
    return false;
  }

  if (on && !state.highlight) {
    enableHighlight();
  } else if (!on && state.highlight) {
    disableHighlight();
  }

  writePreference(HIGHLIGHT_KEY, on ? '1' : '0');
  if (state.toolbar) {
    state.toolbar.update();
  }

  return !!state.highlight;
}

function isOwnNode(node) {
  return !!node && node.nodeType === 1 && node.hasAttribute('data-blazor-profiler');
}

function enableHighlight() {
  if (typeof MutationObserver === 'undefined' || typeof requestAnimationFrame === 'undefined') {
    return;
  }

  const host = document.createElement('div');
  host.setAttribute('data-blazor-profiler', 'highlight');
  host.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483646;';
  const canvas = document.createElement('canvas');
  canvas.style.cssText = 'width:100%;height:100%;display:block;';
  host.attachShadow({ mode: 'open' }).appendChild(canvas);

  const hl = { host, canvas, pending: new Set(), boxes: [], counts: new WeakMap(), frame: 0, observer: null };
  hl.observer = new MutationObserver(records => {
    let n = 0;
    for (const r of records) {
      // Ignore the profiler's own elements (re-attached after enhanced navigation, for example).
      if (isOwnNode(r.target) || (r.type === 'childList' && ([...r.addedNodes].some(isOwnNode) || [...r.removedNodes].some(isOwnNode)))) {
        continue;
      }

      n++;
      if (hl.pending.size >= MAX_BOXES_PER_FRAME) {
        continue;
      }

      if (r.type === 'childList') {
        let added = false;
        for (const node of r.addedNodes) {
          const el = node.nodeType === 1 ? node : node.parentElement;
          if (el) {
            hl.pending.add(el);
            added = true;
          }
        }

        if (!added && r.target.nodeType === 1) {
          hl.pending.add(r.target);
        }
      } else {
        const el = r.target.nodeType === 1 ? r.target : r.target.parentElement;
        if (el) {
          hl.pending.add(el);
        }
      }
    }

    if (n && state) {
      state.live.mutations.push({ t: performance.now(), n });
    }

    if (hl.pending.size && !hl.frame) {
      hl.frame = requestAnimationFrame(() => drawHighlights(hl));
    }
  });
  hl.observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
  document.documentElement.appendChild(host);
  state.highlight = hl;
}

function disableHighlight() {
  const hl = state && state.highlight;
  if (!hl) {
    return;
  }

  hl.observer.disconnect();
  cancelAnimationFrame(hl.frame);
  hl.host.remove();
  state.highlight = null;
}

function drawHighlights(hl) {
  hl.frame = 0;
  if (!state || state.highlight !== hl) {
    return;
  }

  if (!hl.host.isConnected) {
    document.documentElement.appendChild(hl.host);
  }

  const now = performance.now();
  for (const el of hl.pending) {
    if (!el.isConnected || el === document.documentElement || el === document.body || isOwnNode(el)) {
      continue;
    }

    const rect = el.getBoundingClientRect();
    if (rect.width < 1 || rect.height < 1 || rect.bottom < 0 || rect.right < 0 || rect.top > innerHeight || rect.left > innerWidth) {
      continue;
    }

    const previous = hl.counts.get(el);
    const count = previous && now - previous.t <= REPEAT_WINDOW_MS ? previous.n + 1 : 1;
    hl.counts.set(el, { n: count, t: now });
    hl.boxes.push({ x: rect.left, y: rect.top, w: rect.width, h: rect.height, born: now, count });
  }

  hl.pending.clear();
  if (hl.boxes.length > MAX_BOXES_PER_FRAME * 2) {
    hl.boxes.splice(0, hl.boxes.length - MAX_BOXES_PER_FRAME * 2);
  }

  const dpr = window.devicePixelRatio || 1;
  const width = Math.round(innerWidth * dpr);
  const height = Math.round(innerHeight * dpr);
  if (hl.canvas.width !== width || hl.canvas.height !== height) {
    hl.canvas.width = width;
    hl.canvas.height = height;
  }

  const g = hl.canvas.getContext('2d');
  if (!g) {
    return;
  }

  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, innerWidth, innerHeight);
  g.font = '10px system-ui, sans-serif';
  hl.boxes = hl.boxes.filter(b => now - b.born < FLASH_MS);
  for (const b of hl.boxes) {
    const stroke = HEAT[Math.min(HEAT.length - 1, b.count - 1)];
    g.globalAlpha = Math.max(0, 1 - (now - b.born) / FLASH_MS);
    g.strokeStyle = stroke;
    g.lineWidth = b.count > 1 ? 2 : 1;
    g.strokeRect(b.x + 0.5, b.y + 0.5, Math.max(1, b.w - 1), Math.max(1, b.h - 1));
    if (b.count > 1 && b.w > 18 && b.h > 12) {
      const label = `×${b.count}`;
      const w = g.measureText(label).width + 6;
      g.fillStyle = stroke;
      g.fillRect(b.x + b.w - w, b.y, w, 12);
      g.fillStyle = '#111';
      g.fillText(label, b.x + b.w - w + 3, b.y + 10);
    }
  }

  g.globalAlpha = 1;
  if (hl.boxes.length) {
    hl.frame = requestAnimationFrame(() => drawHighlights(hl));
  }
}

// ------------------------------------------------------------------------------------------------ Startup and web vitals

const FRAMEWORK_PATH = '/_framework/';
const STARTUP_DELAY_MS = 3000;
const LARGEST_FILES = 15;
// Files at least this large that arrive without content encoding are reported as uncompressed.
const COMPRESSIBLE_MIN_BYTES = 20 * 1024;

/**
 * Reports page startup once per document: navigation and paint timing, when this client became interactive, and the
 * Blazor framework download (_framework files: count, transferred and decoded bytes, cache hits, compression, largest
 * files). Waits a few seconds so in-flight framework downloads (for example Interactive Auto) are included.
 */
function scheduleStartup() {
  if (startupReported) {
    return;
  }

  // The first client of the document marks when the page became interactive; a client re-created before the report was
  // sent (for example by an enhanced navigation) reports it instead.
  firstInteractiveAt ??= performance.now();
  const interactiveAt = firstInteractiveAt;
  state.startupTimer = setTimeout(() => {
    try {
      const entry = collectStartup(interactiveAt);
      if (entry) {
        startupReported = true;
        push(entry);
      }
    } catch { /* never break the page */ }
  }, Math.max(0, STARTUP_DELAY_MS - (performance.now() - interactiveAt)));
}

function collectStartup(interactiveAt) {
  if (typeof performance.getEntriesByType !== 'function') {
    return null;
  }

  const navigation = performance.getEntriesByType('navigation')[0];
  const fcp = performance.getEntriesByName('first-contentful-paint')[0];
  const resources = performance.getEntriesByType('resource').filter(r => r.name.includes(FRAMEWORK_PATH));
  let transfer = 0;
  let decoded = 0;
  let cached = 0;
  let uncompressed = 0;
  let first = Infinity;
  let last = 0;
  for (const r of resources) {
    transfer += r.transferSize || 0;
    decoded += r.decodedBodySize || 0;
    if (r.decodedBodySize > 0 && r.transferSize === 0) {
      cached++;
    } else if (r.decodedBodySize >= COMPRESSIBLE_MIN_BYTES && r.encodedBodySize >= r.decodedBodySize && /\.(wasm|dll|js|json|dat|pdb|webcil)(\?|$)/i.test(r.name)) {
      uncompressed++;
    }

    first = Math.min(first, r.startTime);
    last = Math.max(last, r.responseEnd || r.startTime + r.duration);
  }

  const names = resources.map(r => r.name);
  const kind = names.some(n => /dotnet\.native\.[^/]*wasm|dotnet\.wasm/.test(n))
    ? (names.some(n => /blazor\.web\.js/.test(n)) ? 'webapp-webassembly' : 'webassembly')
    : names.some(n => /blazor\.web\.js/.test(n)) ? 'webapp' : names.some(n => /blazor\.server\.js/.test(n)) ? 'server' : 'unknown';
  const largest = [...resources].sort((a, b) => (b.decodedBodySize || 0) - (a.decodedBodySize || 0)).slice(0, LARGEST_FILES).map(r => ({
    n: cut(r.name.slice(r.name.lastIndexOf('/') + 1).split('?')[0], 128),
    ts: r.transferSize || 0,
    ds: r.decodedBodySize || 0,
    d: Math.max(0, r.duration),
    c: r.decodedBodySize > 0 && r.transferSize === 0,
  }));

  return {
    k: 's',
    n: navigation ? cut(navigation.type, 32) : 'unknown',
    t: performance.timeOrigin,
    d: interactiveAt,
    ttfb: navigation ? Math.max(0, navigation.responseStart) : 0,
    fcp: fcp ? fcp.startTime : 0,
    dcl: navigation ? Math.max(0, navigation.domContentLoadedEventEnd) : 0,
    ld: navigation ? Math.max(0, navigation.loadEventEnd) : 0,
    ia: interactiveAt,
    fc: resources.length,
    ft: transfer,
    fd: decoded,
    fca: cached,
    fu: uncompressed,
    fdl: resources.length ? Math.max(0, last - first) : 0,
    rk: kind,
    lf: largest,
    ...deviceInfo(),
  };
}

// The conditions the page ran under, so sessions recorded on different machines or networks are not compared blindly.
// Coarse values only (core count, memory bucket, CPU tier, connection class); nothing identifies the user.
function deviceInfo() {
  const nav = typeof navigator === 'undefined' ? {} : navigator;
  const connection = nav.connection || {};
  return {
    hc: typeof nav.hardwareConcurrency === 'number' ? nav.hardwareConcurrency : undefined,
    dm: typeof nav.deviceMemory === 'number' ? nav.deviceMemory : undefined,
    cpu: typeof nav.cpuPerformance === 'number' ? nav.cpuPerformance : undefined,
    ect: typeof connection.effectiveType === 'string' ? cut(connection.effectiveType, 16) : undefined,
  };
}

/** Observes Largest Contentful Paint and Cumulative Layout Shift (web-vitals session windows: 1 s gap, 5 s maximum). */
function observeVitals() {
  observe('largest-contentful-paint', {}, entries => {
    const last = entries[entries.length - 1];
    if (last) {
      state.vitals.lcp = { value: last.startTime, tag: last.element && last.element.tagName ? last.element.tagName.toLowerCase() : undefined };
    }
  });

  let windowValue = 0;
  let windowStart = 0;
  let windowLast = 0;
  observe('layout-shift', {}, entries => {
    for (const e of entries) {
      if (e.hadRecentInput) {
        continue;
      }

      if (windowValue > 0 && (e.startTime - windowLast > 1000 || e.startTime - windowStart > 5000)) {
        windowValue = 0;
      }

      if (windowValue === 0) {
        windowStart = e.startTime;
      }

      windowValue += e.value;
      windowLast = e.startTime;
      if (windowValue > state.vitals.cls) {
        state.vitals.cls = windowValue;
        const source = Array.isArray(e.sources) ? e.sources.find(s => s.node && s.node.nodeType === 1) : null;
        state.vitals.clsSource = source ? cut(source.node.tagName.toLowerCase(), 32) : state.vitals.clsSource;
      }
    }
  });
}

/** Queues changed web vitals (LCP, CLS, and the INP estimate) for the next batch. */
function queueVitals() {
  if (!state || !state.collect || !state.vitals) {
    return;
  }

  const queue = (name, value, tag) => {
    if (value == null || !(value >= 0) || state.vitals.sent[name] === value) {
      return;
    }

    state.vitals.sent[name] = value;
    push({ k: 'v', n: name, t: performance.timeOrigin + performance.now(), d: 0, v: value, tag });
  };

  if (state.vitals.lcp) {
    queue('LCP', Math.round(state.vitals.lcp.value * 10) / 10, state.vitals.lcp.tag);
  }

  if (state.vitals.cls > 0) {
    queue('CLS', Math.round(state.vitals.cls * 10000) / 10000, state.vitals.clsSource);
  }

  const live = state.live;
  const inp = live ? estimateInp([...live.interactions.values()], live.count) : null;
  if (inp != null) {
    queue('INP', Math.round(inp), undefined);
  }

  // DOM size: the number of elements in the document (Lighthouse warns above 800 and fails above 1,400). Reported when it
  // changes by 10% or more, so a list that grows is followed without a report on every flush.
  if (typeof document !== 'undefined' && document.getElementsByTagName) {
    const elements = document.getElementsByTagName('*').length;
    const previous = state.vitals.sent.DOM;
    if (previous === undefined || Math.abs(elements - previous) >= Math.max(50, previous * 0.1)) {
      queue('DOM', elements, undefined);
    }
  }
}

// ------------------------------------------------------------------------------------------------ DevTools tracks
// Writes profiler events into the browser's Performance panel with the DevTools extensibility API: entries appear in a
// "Blazor" track group while a recording runs ("Show custom tracks" in the capture settings; Chrome/Edge 128+). Other
// browsers record them as plain user timings. Entries are removed from the performance timeline right away so the
// buffer does not grow; DevTools has already captured them.

/**
 * Receives a JSON array of [name, track, startEpochMs, endEpochMs, color, tooltip, properties]; an empty track means a
 * marker (a vertical line across the timeline, for example a navigation).
 */
export function devtools(json) {
  if (typeof performance === 'undefined' || typeof performance.measure !== 'function') {
    return;
  }

  let entries;
  try {
    entries = JSON.parse(json);
  } catch {
    return;
  }

  const origin = performance.timeOrigin;
  for (const entry of entries) {
    const [name, track, startEpoch, endEpoch, color, tooltipText, properties] = entry;
    const start = startEpoch - origin;
    const end = Math.max(endEpoch - origin, start);
    if (!(start >= 0) || !(end >= 0)) {
      continue;
    }

    try {
      if (!track) {
        performance.mark(name, { startTime: start, detail: { devtools: { dataType: 'marker', color, tooltipText, properties } } });
        performance.clearMarks(name);
      } else {
        performance.measure(name, { start, end, detail: { devtools: { dataType: 'track-entry', track, trackGroup: 'Blazor', color, tooltipText, properties } } });
        performance.clearMeasures(name);
      }
    } catch { /* invalid times or unsupported options: skip the entry */ }
  }
}
