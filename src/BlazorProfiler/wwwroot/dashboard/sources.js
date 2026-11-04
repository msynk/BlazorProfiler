// Data sources for the dashboard. Every source exposes the same asynchronous API, so views do not care whether data
// comes from a server (HTTP), a WebAssembly app in another tab (BroadcastChannel), or an imported trace file.

const CHANNEL_NAME = 'blazor-profiler';
const MAX_FILE_BYTES = 256 * 1024 * 1024;
const MAX_FILE_EVENTS = 1_000_000;

class SourceError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

async function errorFrom(response) {
  let message = `${response.status} ${response.statusText}`;
  try {
    const body = await response.json();
    if (body && body.error) {
      message = body.error;
    }
  } catch { /* not JSON */ }

  if (response.status === 404) {
    message = 'Not found. The profiler endpoint may be disabled, or you are not authorized to access it.';
  }

  return new SourceError(message, response.status);
}

/** Server source: the HTTP API mapped by MapBlazorProfiler. */
export class ApiSource {
  constructor(base) {
    this.kind = 'api';
    this.base = base.replace(/\/$/, '');
    this.label = 'Server';
    this.readOnly = false;
  }

  async get(path) {
    const response = await fetch(this.base + path, { credentials: 'same-origin', headers: { Accept: 'application/json' }, cache: 'no-store' });
    if (!response.ok) {
      throw await errorFrom(response);
    }

    return response.status === 204 ? null : response.json();
  }

  async send(method, path, body, contentType = 'application/json') {
    const headers = { 'X-Blazor-Profiler': '1', Accept: 'application/json' };
    if (body !== undefined) {
      headers['Content-Type'] = contentType;
    }

    const response = await fetch(this.base + path, {
      method,
      credentials: 'same-origin',
      headers,
      body: body === undefined ? undefined : (contentType === 'application/json' ? JSON.stringify(body) : body),
    });
    if (!response.ok) {
      throw await errorFrom(response);
    }

    return response.status === 204 ? null : response.json();
  }

  info() { return this.get('/info'); }
  events(id, after, max) { return this.get(`/sessions/${encodeURIComponent(id)}/events?after=${after}&max=${max}`); }
  summary(id) { return this.get(`/sessions/${encodeURIComponent(id)}/summary`); }
  findings(id) { return this.get(`/sessions/${encodeURIComponent(id)}/findings`); }
  operation(id, operationId) { return this.get(`/sessions/${encodeURIComponent(id)}/operations/${operationId}`); }
  circuits() { return this.get('/circuits'); }
  control(id, action, name) { return this.send('POST', `/sessions/${encodeURIComponent(id)}/${action}${name ? `?name=${encodeURIComponent(name)}` : ''}`); }
  start(name) { return this.send('POST', '/sessions', { name }); }
  remove(id) { return this.send('DELETE', `/sessions/${encodeURIComponent(id)}`); }
  suppress(fingerprint, kind, note) { return this.send('POST', '/suppressions', { fingerprint, kind, note }); }
  unsuppress(fingerprint) { return this.send('DELETE', `/suppressions?fingerprint=${encodeURIComponent(fingerprint)}`); }

  async importTrace(file) {
    return this.send('POST', '/import', file, 'application/octet-stream');
  }

  async exportTrace(id, format) {
    const response = await fetch(`${this.base}/sessions/${encodeURIComponent(id)}/export?format=${format}`, { credentials: 'same-origin' });
    if (!response.ok) {
      throw await errorFrom(response);
    }

    const disposition = response.headers.get('Content-Disposition') || '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    return { blob: await response.blob(), fileName: match ? decodeURIComponent(match[1]) : `blazor-profile.${format}` };
  }
}

/** WebAssembly source: talks to an app tab through a same-origin BroadcastChannel. */
export class BridgeSource {
  constructor(client) {
    this.kind = 'bridge';
    this.client = client;
    this.label = client.title || client.path || 'WebAssembly app';
    this.readOnly = false;
    this.pending = new Map();
    this.nextId = 1;
    this.channel = new BroadcastChannel(CHANNEL_NAME);
    this.channel.onmessage = event => {
      const message = event.data || {};
      if (message.type === 'response' && message.clientId === this.client.clientId) {
        const pending = this.pending.get(message.requestId);
        if (pending) {
          this.pending.delete(message.requestId);
          pending.resolve(message.payload);
        }
      } else if (message.type === 'goodbye' && message.clientId === this.client.clientId) {
        this.disconnected = true;
      }
    };
  }

  static supported() {
    return typeof BroadcastChannel !== 'undefined';
  }

  /** Discovers app tabs that run the profiler and expose the bridge. */
  static discover(timeoutMs = 600) {
    return new Promise(resolve => {
      if (!BridgeSource.supported()) {
        resolve([]);
        return;
      }

      const channel = new BroadcastChannel(CHANNEL_NAME);
      const clients = new Map();
      channel.onmessage = event => {
        const message = event.data || {};
        if (message.type === 'announce' && message.clientId) {
          clients.set(message.clientId, message);
        }
      };
      channel.postMessage({ type: 'discover' });
      setTimeout(() => {
        channel.close();
        resolve([...clients.values()]);
      }, timeoutMs);
    });
  }

  call(payload, timeoutMs = 15000) {
    const requestId = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new SourceError('The app tab did not respond. It may have been closed or reloaded.'));
      }, timeoutMs);
      this.pending.set(requestId, {
        resolve: raw => {
          clearTimeout(timer);
          let response;
          try {
            response = JSON.parse(raw);
          } catch {
            reject(new SourceError('Invalid response from the app tab.'));
            return;
          }

          if (!response.ok) {
            reject(new SourceError(response.error || 'Request failed.'));
          } else {
            resolve(response);
          }
        },
      });
      this.channel.postMessage({ type: 'request', clientId: this.client.clientId, requestId, payload });
    });
  }

  async info() { return (await this.call({ op: 'info' })).info; }
  async events(id, after, max) { return (await this.call({ op: 'events', sessionId: id, after, max })).page; }
  async summary(id) { return (await this.call({ op: 'summary', sessionId: id })).summary; }
  async findings(id) { return (await this.call({ op: 'findings', sessionId: id })).report; }
  async operation(id, operationId) { return (await this.call({ op: 'operation', sessionId: id, operationId })).events; }
  async circuits() { return []; }
  async control(id, action, name) { return (await this.call({ op: 'control', sessionId: id, action, name })).session; }
  async start(name) { return (await this.call({ op: 'start', name })).session; }
  async remove(id) { await this.call({ op: 'remove', sessionId: id }); }
  async suppress(fingerprint, kind, note) { await this.call({ op: 'suppress', fingerprint, kind, note }); }
  async unsuppress(fingerprint) { await this.call({ op: 'unsuppress', fingerprint }); }

  async importTrace(file) {
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }

    return (await this.call({ op: 'import', data: btoa(binary) }, 120000)).session;
  }

  async exportTrace(id, format) {
    const response = await this.call({ op: 'export', sessionId: id, format }, 120000);
    const binary = atob(response.data);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }

    return { blob: new Blob([bytes], { type: response.contentType }), fileName: response.fileName };
  }

  close() {
    this.channel.close();
  }
}

/** Offline source: traces opened from files. Read-only; findings come from the trace. */
export class FileSource {
  constructor() {
    this.kind = 'file';
    this.label = 'Imported files';
    this.readOnly = true;
    this.docs = new Map();
    this.nextId = 1;
  }

  async add(file) {
    if (file.size > MAX_FILE_BYTES) {
      throw new SourceError(`The file is larger than ${MAX_FILE_BYTES / 1024 / 1024} MB.`);
    }

    const buffer = new Uint8Array(await file.arrayBuffer());
    let text;
    if (buffer[0] === 0x1f && buffer[1] === 0x8b) {
      if (typeof DecompressionStream === 'undefined') {
        throw new SourceError('This browser cannot decompress traces. Export as JSON instead.');
      }

      const stream = new Blob([buffer]).stream().pipeThrough(new DecompressionStream('gzip'));
      const reader = stream.getReader();
      const chunks = [];
      let total = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }

        total += value.length;
        if (total > MAX_FILE_BYTES) {
          await reader.cancel();
          throw new SourceError('The decompressed trace is too large.');
        }

        chunks.push(value);
      }

      text = await new Blob(chunks).text();
    } else {
      text = new TextDecoder().decode(buffer);
    }

    let doc;
    try {
      doc = JSON.parse(text);
    } catch {
      throw new SourceError('The file is not valid JSON.');
    }

    validateTrace(doc);
    const id = `file-${this.nextId++}`;
    doc.__fileName = file.name;
    this.docs.set(id, doc);
    return id;
  }

  stats(id, doc) {
    const clock = doc.clock;
    const events = doc.events;
    const first = events.length ? Math.min(...events.slice(0, 50000).map(e => e.timestamp)) : clock.anchorTimestamp;
    return {
      id,
      name: `${doc.metadata?.sessionName || 'Trace'} (${doc.__fileName})`,
      state: 'Imported',
      createdAt: doc.metadata?.createdAt,
      durationMs: doc.metadata?.durationMs || 0,
      storedEvents: events.length,
      storedBytes: 0,
      acceptedEvents: events.length,
      evictedEvents: doc.metadata?.evictedEvents || 0,
      lastSequence: events.length,
      isImported: true,
      clockFrequency: clock.frequency,
      clockAnchorTimestamp: clock.anchorTimestamp,
      clockAnchorTime: clock.anchorTime,
      startTimestamp: first,
      fileImported: true,
    };
  }

  async info() {
    return {
      source: 'file',
      sessions: [...this.docs.entries()].map(([id, doc]) => this.stats(id, doc)),
      health: null,
      rules: [],
      canViewAllCircuits: true,
    };
  }

  async events(id, after, max) {
    const doc = this.docs.get(id);
    const slice = doc.events.slice(after, after + max);
    return { events: slice, cursor: after + slice.length, gap: false, hasMore: after + slice.length < doc.events.length };
  }

  async summary(id) { return this.docs.get(id).summary || null; }

  async findings(id) {
    const doc = this.docs.get(id);
    return { findings: doc.findings || [], rules: [], ruleErrors: {}, analyzedEvents: doc.events.length, fromTrace: true };
  }

  async operation(id, operationId) { return this.docs.get(id).events.filter(e => e.operationId === operationId); }
  async circuits() { return []; }

  metadata(id) {
    const doc = this.docs.get(id);
    return doc ? { metadata: doc.metadata, collectors: doc.collectors || [] } : null;
  }

  async remove(id) { this.docs.delete(id); }

  async exportTrace(id) {
    const doc = this.docs.get(id);
    const copy = { ...doc };
    delete copy.__fileName;
    return { blob: new Blob([JSON.stringify(copy)], { type: 'application/json' }), fileName: doc.__fileName.replace(/(\.gz|\.bptrace)?$/, '') + '.bptrace.json' };
  }
}

function validateTrace(doc) {
  if (!doc || doc.format !== 'blazor-profiler-trace') {
    throw new SourceError('Not a Blazor Profiler trace (unknown format).');
  }

  if (!Number.isInteger(doc.formatVersion) || doc.formatVersion < 1 || doc.formatVersion > 1) {
    throw new SourceError(`Unsupported trace format version ${doc.formatVersion}.`);
  }

  if (!doc.clock || !(doc.clock.frequency > 0)) {
    throw new SourceError('The trace clock is missing or invalid.');
  }

  if (!Array.isArray(doc.events)) {
    throw new SourceError('The trace has no event list.');
  }

  if (doc.events.length > MAX_FILE_EVENTS) {
    throw new SourceError(`The trace has more than ${MAX_FILE_EVENTS} events.`);
  }

  for (const e of doc.events) {
    if (!e || typeof e.type !== 'string' || !Number.isFinite(e.timestamp ?? 0) || (e.duration ?? 0) < 0) {
      throw new SourceError('The trace contains an invalid event.');
    }
  }
}
