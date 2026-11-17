// Comparison of two time windows of one session ("before and after I clicked X", "first load vs. second load").
// Builds summaries with the same shape as the server's SessionSummary from the events inside each window, so the session
// comparison table can show them side by side.

// Weighted percentile: each value stands for its sample weight, consistent with the weighted counts and means.
function percentile(sorted, total, p) {
  if (!sorted.length) {
    return 0;
  }

  const target = (p / 100) * total;
  let cumulative = 0;
  for (const x of sorted) {
    cumulative += x.w;
    if (cumulative >= target - 1e-9) {
      return x.ms;
    }
  }

  return sorted[sorted.length - 1].ms;
}

function durations(list) {
  const sorted = [...list].sort((a, b) => a.ms - b.ms);
  const count = list.reduce((n, x) => n + x.w, 0);
  const totalMs = list.reduce((n, x) => n + x.ms * x.w, 0);
  return {
    count,
    totalMs,
    meanMs: count ? totalMs / count : 0,
    p50Ms: percentile(sorted, count, 50),
    p95Ms: percentile(sorted, count, 95),
    maxMs: sorted.length ? sorted[sorted.length - 1].ms : 0,
  };
}

function group(map, key, create) {
  let value = map.get(key);
  if (!value) {
    value = create();
    map.set(key, value);
  }

  return value;
}

/**
 * Summarizes the events of one window (they must already be filtered to the window with eventsInWindow). Interval data
 * (runtime samples) is prorated to the part of the interval inside the window; other events count where they start.
 */
export function summarizeWindow(ctx, events, window) {
  const weight = e => (e.sampleWeight > 0 ? e.sampleWeight : 1);
  const item = e => ({ ms: ctx.ticksToMs(e.duration || 0), w: weight(e) });
  const batches = [];
  let updated = 0;
  const components = new Map();
  const handlers = new Map();
  const endpoints = new Map();
  const interop = new Map();
  const queries = new Map();
  const interactions = [];
  const runtime = { allocatedBytes: 0, gcPauseMs: 0, gcPauseUnavailable: true };
  const component = type => group(components, type || '(unknown)', () => ({ componentType: type || '(unknown)', instrumented: false, renders: [], params: [], retained: 0 }));

  for (const e of events) {
    switch (e.type) {
      case 'renderBatch':
        batches.push(item(e));
        updated += (e.updatedComponentCount || 0) * weight(e);
        break;
      case 'componentRender': {
        const c = component(e.componentType);
        c.instrumented = true;
        c.renders.push(item(e));
        break;
      }
      case 'parametersSet':
        component(e.componentType).params.push(item(e));
        break;
      case 'componentRetained':
        component(e.componentType).retained += weight(e);
        break;
      case 'eventHandler':
        group(handlers, `${e.componentType}.${e.methodName}`, () => []).push(item(e));
        break;
      case 'http':
        group(endpoints, e.endpoint || e.url || '?', () => []).push(item(e));
        break;
      case 'jsInterop':
        group(interop, e.identifier || '?', () => []).push(item(e));
        break;
      case 'dbCommand':
        group(queries, e.commandText || '?', () => []).push(item(e));
        break;
      case 'browserInteraction':
        interactions.push(item(e));
        break;
      case 'runtime': {
        const share = overlapShare(ctx, e, window);
        runtime.allocatedBytes += (e.allocatedBytes || 0) * share;
        if (e.gcPauseTicks != null) {
          runtime.gcPauseUnavailable = false;
          runtime.gcPauseMs += ctx.ticksToMs(e.gcPauseTicks) * share;
        }

        break;
      }
      default:
        break;
    }
  }

  const batchStats = durations(batches);
  return {
    renderBatches: { durations: batchStats, totalUpdatedComponents: updated },
    components: [...components.values()].map(c => ({
      componentType: c.componentType,
      instrumented: c.instrumented,
      renders: durations(c.renders),
      parametersSets: durations(c.params),
      retained: c.retained,
    })),
    handlers: [...handlers.values()].map(list => ({ durations: durations(list) })),
    endpoints: [...endpoints.entries()].map(([endpoint, list]) => ({ endpoint, durations: durations(list) })),
    interop: [...interop.entries()].map(([identifier, list]) => ({ identifier, durations: durations(list) })),
    queries: [...queries.entries()].map(([commandText, list]) => ({ commandText, durations: durations(list) })),
    browserInteractions: durations(interactions),
    runtime,
  };
}

// The fraction of an interval event inside the window (1 without a window or for instantaneous events).
function overlapShare(ctx, e, window) {
  if (!window || !e.duration) {
    return 1;
  }

  const start = ctx.toMs(e.timestamp);
  const end = ctx.toMs(e.timestamp + e.duration);
  const inside = Math.min(end, window.end) - Math.max(start, window.start);
  return end > start ? Math.max(0, Math.min(1, inside / (end - start))) : 1;
}

/**
 * Returns the events of a window given in session milliseconds: events that start inside it (half-open, so adjacent
 * windows never count an event twice), plus runtime samples that overlap it (prorated by summarizeWindow).
 */
export function eventsInWindow(ctx, events, window) {
  return events.filter(e => {
    const start = ctx.toMs(e.timestamp);
    if (e.type === 'runtime') {
      const end = ctx.toMs(e.timestamp + (e.duration || 0));
      return end > window.start && start < window.end;
    }

    return start >= window.start && start < window.end;
  });
}
