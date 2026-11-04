// Blazor Profiler browser module.
// Observes browser performance entries and forwards them to .NET in small batches. In WebAssembly it also answers
// dashboard requests received over a same-origin BroadcastChannel. It never reads element content, input values,
// cookies, or storage.

const CHANNEL_NAME = 'blazor-profiler';
const MAX_BUFFER = 2000;
const MAX_ENTRIES_PER_BATCH = 1000;
const MAX_BATCHES_PER_FLUSH = 8;
// Blazor Server's default SignalR limit is 32 KB per incoming message; the batch travels as an escaped string argument,
// so the default budget leaves room for escaping, multi-byte characters, and the invocation envelope.
const DEFAULT_MAX_BATCH_CHARS = 16000;

let state = null;

export function start(dotNetRef, optionsJson) {
  stop();
  const options = typeof optionsJson === 'string' ? JSON.parse(optionsJson) : (optionsJson || {});
  state = {
    ref: dotNetRef,
    options,
    buffer: [],
    observers: [],
    timer: 0,
    channel: null,
    clientId: (globalThis.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Math.random()).slice(2),
    keyHandler: null,
    launcher: null,
    interactions: new Map(),
  };

  if (options.collect !== false) {
    observeInteractions(options.interactionThresholdMs || 40);
    observeLongFrames();
    observeResources();
    state.timer = setInterval(() => flush(false), options.flushIntervalMs || 2000);
  }

  if (options.bridge) {
    startBridge();
  }

  if (options.shortcut) {
    installShortcut(options.shortcut);
  }

  if (options.showLauncher) {
    installLauncher();
  }

  return performance.timeOrigin + performance.now();
}

export function stop() {
  if (!state) {
    return;
  }

  for (const observer of state.observers) {
    try { observer.disconnect(); } catch { /* ignore */ }
  }

  clearInterval(state.timer);
  flush(true);
  if (state.channel) {
    state.channel.close();
  }

  if (state.keyHandler) {
    window.removeEventListener('keydown', state.keyHandler);
  }

  if (state.launcher) {
    state.launcher.remove();
  }

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

  try {
    const observer = new PerformanceObserver(list => {
      try { callback(list.getEntries()); } catch { /* never break the page */ }
    });
    observer.observe({ type, buffered: true, ...init });
    state.observers.push(observer);
  } catch { /* unsupported options */ }
}

function push(entry) {
  if (!state) {
    return;
  }

  if (state.buffer.length >= MAX_BUFFER) {
    state.buffer.shift();
  }

  state.buffer.push(entry);
}

function epoch(startTime) {
  return performance.timeOrigin + startTime;
}

// Long strings are cut here as well as in .NET, so a single entry can never exceed the batch budget.
function cut(value, max) {
  return typeof value === 'string' && value.length > max ? value.slice(0, max) : value;
}

function observeInteractions(threshold) {
  observe('event', { durationThreshold: Math.max(16, threshold) }, entries => {
    for (const e of entries) {
      // Only discrete interactions carry an interactionId; keep the longest entry per interaction.
      if (!e.interactionId || e.duration < threshold) {
        continue;
      }

      const existing = state.interactions.get(e.interactionId);
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
        Object.assign(existing, entry);
      } else {
        state.interactions.set(e.interactionId, entry);
        push(entry);
      }
    }
  });
}

function observeLongFrames() {
  if (supports('long-animation-frame')) {
    observe('long-animation-frame', {}, entries => {
      for (const e of entries) {
        let script;
        if (Array.isArray(e.scripts) && e.scripts.length > 0) {
          const longest = e.scripts.reduce((a, b) => (b.duration > a.duration ? b : a));
          script = longest.sourceURL || longest.invoker || undefined;
        }

        push({ k: 'l', n: 'long-animation-frame', t: epoch(e.startTime), d: e.duration, b: e.blockingDuration || 0, s: cut(script, 512) });
      }
    });
  } else {
    observe('longtask', {}, entries => {
      for (const e of entries) {
        push({ k: 'l', n: 'longtask', t: epoch(e.startTime), d: e.duration, b: Math.max(0, e.duration - 50) });
      }
    });
  }
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
  if (!state || state.buffer.length === 0) {
    return;
  }

  const budget = state.options.maxBatchChars || DEFAULT_MAX_BATCH_CHARS;
  const sent = new Set();
  for (let batches = 0; state.buffer.length > 0 && (all || batches < MAX_BATCHES_PER_FLUSH); batches++) {
    const parts = [];
    let size = 2;
    let taken = 0;
    while (taken < state.buffer.length && parts.length < MAX_ENTRIES_PER_BATCH) {
      const entry = state.buffer[taken];
      const text = JSON.stringify(entry);
      // The batch is passed as a string argument and escaped again on the wire, so measure the escaped length.
      const cost = JSON.stringify(text).length - 1;
      if (cost > budget) {
        // Cannot be sent without exceeding the budget; drop it (entries are truncated, so this is not expected).
        taken++;
        sent.add(entry);
        continue;
      }

      if (parts.length > 0 && size + cost > budget) {
        break;
      }

      parts.push(text);
      sent.add(entry);
      size += cost;
      taken++;
    }

    state.buffer.splice(0, taken);
    if (parts.length > 0) {
      state.ref.invokeMethodAsync('BlazorProfiler.Ingest', `[${parts.join(',')}]`).catch(() => { /* circuit gone */ });
    }
  }

  // Interactions already sent can no longer be updated in place; later entries for them start a new record.
  for (const [id, entry] of state.interactions) {
    if (sent.has(entry)) {
      state.interactions.delete(id);
    }
  }
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

    let payload;
    try {
      payload = await state.ref.invokeMethodAsync('BlazorProfiler.Api', JSON.stringify(message.payload || {}));
    } catch (error) {
      payload = JSON.stringify({ ok: false, error: String(error && error.message || error) });
    }

    channel.postMessage({ type: 'response', clientId: state.clientId, requestId: message.requestId, payload });
  };

  announce();
  window.addEventListener('pagehide', () => {
    try { channel.postMessage({ type: 'goodbye', clientId: state && state.clientId }); } catch { /* ignore */ }
  });
}

function installShortcut(shortcut) {
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
      openDashboard();
    }
  };

  window.addEventListener('keydown', handler);
  state.keyHandler = handler;
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
